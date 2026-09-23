using System.Text.RegularExpressions;
using UglyToad.PdfPig;
using UglyToad.PdfPig.Content;

namespace Soltec.DocParser.Services
{
    public class TableColumn
    {
        public string Name = "";
        public double Left;
        public double Right;
    }

    public class ItemRow
    {
        public string Descripcion = "";
        // nombre de columna -> texto (puede tener más de una palabra, p.ej "6754817,62")
        public Dictionary<string, string> Columnas = new();
    }

    public class ExtractedPage
    {
        public string LineText { get; set; } = "";
        public List<PositionedWord> Words { get; set; } = new();
    }

    // Extracción de texto/tabla basada en las coordenadas (x,y) reales de cada palabra de la
    // página (de PdfPig, para PDF con texto embebido, o de OCR, para imágenes -ver
    // OcrExtraction-), en vez de depender del orden en que el texto quedó codificado. Esto es lo
    // que permite separar de forma confiable la columna de "Producto/Servicio" -que suele
    // partirse en varias líneas- de las columnas numéricas de la fila de datos, sin importar cuál
    // de las dos fuentes dio las palabras: todo lo de acá para abajo trabaja sobre PositionedWord.
    public static class PdfPigExtraction
    {
        public static ExtractedPage ExtractPage(Page page)
        {
            var words = page.GetWords()
                .Select(w => new PositionedWord
                {
                    Text = w.Text,
                    BoundingBox = new WordBox { Left = w.BoundingBox.Left, Top = w.BoundingBox.Top, Right = w.BoundingBox.Right, Bottom = w.BoundingBox.Bottom }
                })
                .ToList();
            var lines = GroupIntoLines(words);
            var lineText = string.Join("\n", lines.Select(l => string.Join(" ", l.OrderBy(w => w.BoundingBox.Left).Select(w => w.Text))));
            return new ExtractedPage { LineText = lineText, Words = words };
        }

        // Abre el PDF y devuelve solo las páginas con contenido distinto entre sí -las facturas
        // ARCA/AFIP suelen repetir el mismo comprobante 2 o 3 veces ("ORIGINAL"/"DUPLICADO"/
        // "TRIPLICADO"), que no son ítems nuevos sino copias físicas del mismo documento.
        public static List<ExtractedPage> ExtractDedupedPages(byte[] pdfBytes)
        {
            using var doc = PdfDocument.Open(pdfBytes);
            var paginas = new List<ExtractedPage>();
            for (int i = 1; i <= doc.NumberOfPages; i++)
                paginas.Add(ExtractPage(doc.GetPage(i)));

            var vistos = new HashSet<string>();
            var unicas = new List<ExtractedPage>();
            foreach (var p in paginas)
            {
                var normalizado = Regex.Replace(p.LineText.Trim(), @"^\s*(ORIGINAL|DUPLICADO|TRIPLICADO|CUADRUPLICADO)\s*", "", RegexOptions.IgnoreCase);
                if (vistos.Add(normalizado))
                    unicas.Add(p);
            }
            return unicas;
        }

        // Agrupa palabras en "líneas visuales" según su coordenada Top, tolerando pequeñas
        // diferencias de línea base entre fuentes/tamaños dentro de la misma fila.
        public static List<List<PositionedWord>> GroupIntoLines(List<PositionedWord> words, double tolerance = 3.0)
        {
            var sorted = words.OrderByDescending(w => w.BoundingBox.Top).ToList();
            var lines = new List<List<PositionedWord>>();
            foreach (var w in sorted)
            {
                var line = lines.LastOrDefault();
                if (line != null && Math.Abs(line[0].BoundingBox.Top - w.BoundingBox.Top) <= tolerance)
                    line.Add(w);
                else
                    lines.Add(new List<PositionedWord> { w });
            }
            return lines;
        }

        // Reconstruye los ítems de la tabla de un comprobante ARCA/AFIP usando las posiciones
        // reales de cada palabra. tablaTop/tablaBottom acotan la franja de la página donde vive
        // la tabla (desde la línea de encabezado de columnas hasta la línea de "Subtotal:"/
        // "Importe..."), ambas inclusive.
        public static List<ItemRow> ExtractItemRows(List<PositionedWord> words, double tablaTop, double tablaBottom)
        {
            var enTabla = words.Where(w => w.BoundingBox.Top <= tablaTop && w.BoundingBox.Top >= tablaBottom).ToList();

            // Ancla cada columna a la posición X de inicio de su etiqueta de encabezado.
            var headerAnchors = new List<(string label, double left)>();
            void AddAnchor(string label, Func<PositionedWord, bool> match)
            {
                var w = enTabla.FirstOrDefault(match);
                if (w != null) headerAnchors.Add((label, w.BoundingBox.Left));
            }
            AddAnchor("Descripcion", w => w.Text.Equals("Código", StringComparison.OrdinalIgnoreCase) || w.Text.Equals("Codigo", StringComparison.OrdinalIgnoreCase));
            AddAnchor("Cantidad", w => w.Text.Equals("Cantidad", StringComparison.OrdinalIgnoreCase));
            AddAnchor("UM", w => w.Text.Equals("U.", StringComparison.OrdinalIgnoreCase));
            AddAnchor("Precio", w => w.Text.Equals("Precio", StringComparison.OrdinalIgnoreCase));
            AddAnchor("BonifPct", w => w.Text == "%");
            AddAnchor("ImpBonif", w => w.Text.Equals("Imp.", StringComparison.OrdinalIgnoreCase));
            AddAnchor("Alicuota", w => w.Text.Equals("Alicuota", StringComparison.OrdinalIgnoreCase));

            // "Subtotal" aparece dos veces en las facturas tipo A/FCE (neto y con IVA).
            var subtotalAnchors = enTabla.Where(w => w.Text.Equals("Subtotal", StringComparison.OrdinalIgnoreCase))
                .Select(w => w.BoundingBox.Left).OrderBy(x => x).ToList();
            for (int i = 0; i < subtotalAnchors.Count; i++)
                headerAnchors.Add(($"Subtotal{i}", subtotalAnchors[i]));

            headerAnchors = headerAnchors.OrderBy(a => a.left).ToList();
            if (headerAnchors.Count == 0 || !headerAnchors.Any(a => a.label == "Descripcion"))
                return new(); // no se pudo ubicar la tabla (formato no reconocido)

            // El encabezado puede ocupar 1 o 2 líneas visuales según el comprobante. Toda palabra
            // de esas líneas debe quedar excluida de la ventana de ítems (si no, palabras sueltas
            // del encabezado, p.ej. "Producto"/"Servicio", se filtran como si fueran descripción).
            var headerLines = GroupIntoLines(enTabla, 3.0).Where(l => l.Any(w =>
                w.Text.Equals("Código", StringComparison.OrdinalIgnoreCase) ||
                w.Text.Equals("Codigo", StringComparison.OrdinalIgnoreCase) ||
                w.Text.Equals("Cantidad", StringComparison.OrdinalIgnoreCase) ||
                w.Text.Equals("Precio", StringComparison.OrdinalIgnoreCase))).ToList();
            double contentTop = headerLines.Count > 0
                ? headerLines.SelectMany(l => l).Min(w => w.BoundingBox.Top)
                : tablaTop;

            // El límite derecho de cada columna es el borde izquierdo de la siguiente etiqueta.
            // La primera columna (Descripción) queda abierta hacia la izquierda: su etiqueta
            // ("Código") es solo la primera palabra de un encabezado ancho ("Código Producto /
            // Servicio"), no el borde real de esa columna.
            var columns = new List<TableColumn>();
            for (int i = 0; i < headerAnchors.Count; i++)
            {
                double left = i == 0 ? double.MinValue : headerAnchors[i].left;
                double right = i + 1 < headerAnchors.Count ? headerAnchors[i + 1].left : double.MaxValue;
                columns.Add(new TableColumn { Name = headerAnchors[i].label, Left = left, Right = right });
            }

            string ColumnFor(double x)
            {
                var c = columns.FirstOrDefault(c => x >= c.Left && x < c.Right);
                return c?.Name ?? "?";
            }

            var lines = GroupIntoLines(enTabla, 3.0);
            // Filas de datos = agrupaciones por Y que tengan un token numérico en la columna Cantidad.
            var dataRowTops = lines
                .Where(l => l.Any(w => ColumnFor(w.BoundingBox.Left) == "Cantidad" && Regex.IsMatch(w.Text, @"^\d")))
                .Select(l => l[0].BoundingBox.Top)
                .OrderByDescending(t => t)
                .ToList();

            if (dataRowTops.Count == 0) return new();

            var result = new List<ItemRow>();
            for (int i = 0; i < dataRowTops.Count; i++)
            {
                double top = dataRowTops[i];
                double windowTop = i == 0 ? contentTop : (dataRowTops[i - 1] + top) / 2.0;
                double windowBottom = i == dataRowTops.Count - 1 ? tablaBottom : (top + dataRowTops[i + 1]) / 2.0;

                // Descripción: palabras de la columna "Descripcion" dentro de la ventana de esta
                // fila (antes y/o después de la fila numérica), agrupadas en líneas visuales para
                // no mezclar el orden de palabras entre renglones distintos.
                var descWords = enTabla
                    .Where(w => ColumnFor(w.BoundingBox.Left) == "Descripcion" && w.BoundingBox.Top < windowTop && w.BoundingBox.Top > windowBottom)
                    .ToList();
                var descLines = GroupIntoLines(descWords, 3.0);
                string descripcion = string.Join(" ", descLines.Select(l => string.Join(" ", l.OrderBy(w => w.BoundingBox.Left).Select(w => w.Text)))).Trim();

                // Resto de columnas de la fila de datos, agrupadas por nombre (no una lista
                // posicional): cada valor queda inequívocamente asociado a "Cantidad", "Precio", etc.
                var rowWords = lines.First(l => Math.Abs(l[0].BoundingBox.Top - top) < 0.01);
                var columnas = rowWords
                    .Where(w => ColumnFor(w.BoundingBox.Left) != "Descripcion")
                    .GroupBy(w => ColumnFor(w.BoundingBox.Left))
                    .ToDictionary(g => g.Key, g => string.Join(" ", g.OrderBy(w => w.BoundingBox.Left).Select(w => w.Text)));

                result.Add(new ItemRow { Descripcion = descripcion, Columnas = columnas });
            }

            return result;
        }
    }
}
