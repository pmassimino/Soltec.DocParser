using System.Globalization;
using System.Text.RegularExpressions;
using Soltec.DocParser.Models;
using UglyToad.PdfPig.Content;

namespace Soltec.DocParser.Services
{
    // Parser por reglas para el formato estándar de comprobante electrónico ARCA/AFIP
    // ("Factura A/B/C", "Factura de Crédito Electrónica MiPyMEs", etc. emitidos desde el
    // portal de comprobantes en línea). No cubre formatos de facturación propios de cada
    // proveedor (ver Advertencias del resultado).
    //
    // Los campos de encabezado se extraen por regex sobre el texto reconstruido línea por
    // línea (confiable: las etiquetas y sus valores quedan casi siempre en la misma línea).
    // La tabla de ítems, en cambio, se reconstruye por columnas usando las coordenadas reales
    // de cada palabra (PdfPigExtraction) -no por regex-, porque la columna de descripción suele
    // partirse en varias líneas de forma impredecible.
    public static class FacturaCompraParser
    {
        const string NUM = @"(?:\d{1,3}(?:\.\d{3})+|\d+),\d{2}";

        // Tabla oficial AFIP de códigos de comprobante -> letra. Se usa esto en vez de buscar la
        // letra suelta ("A"/"B"/"C") en el texto, porque esa letra vive en un recuadro visual
        // separado del resto del encabezado (con toda la razón social del emisor en el medio, en
        // el texto extraído), así que no hay ninguna adyacencia confiable para anclar una regex.
        static readonly Dictionary<int, string> CodigoALetra = new()
        {
            [1] = "A", [2] = "A", [3] = "A",
            [6] = "B", [7] = "B", [8] = "B",
            [11] = "C", [12] = "C", [13] = "C",
            [51] = "M", [52] = "M", [53] = "M",
            [201] = "A", [202] = "A", [203] = "A", // FCE MiPyMEs
            [206] = "B", [207] = "B", [208] = "B",
            [211] = "C", [212] = "C", [213] = "C",
        };

        static decimal ParseArNumber(string s)
        {
            s = s.Trim().Replace(".", "").Replace(",", ".");
            return decimal.Parse(s, CultureInfo.InvariantCulture);
        }

        static decimal ParseArNumberOrZero(string? s)
        {
            if (string.IsNullOrWhiteSpace(s)) return 0m;
            var m = Regex.Match(s, NUM);
            return m.Success ? ParseArNumber(m.Value) : 0m;
        }

        static DateTime? ParseArDate(string? s)
        {
            if (string.IsNullOrWhiteSpace(s)) return null;
            return DateTime.TryParseExact(s.Trim(), "dd/MM/yyyy", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d)
                ? d
                : null;
        }

        public static bool EsFormatoArca(string lineText)
        {
            string text = Regex.Replace(lineText, @"\s+", " ");
            return Regex.IsMatch(text, @"Apellido y Nombre\s*/\s*Raz[oó]n Social:")
                && Regex.IsMatch(text, @"C[oó]digo Producto\s*/\s*Servicio");
        }

        public static Factura Parse(string lineText, List<Word> words, string empresaCuit)
        {
            var result = new Factura();

            string text = Regex.Replace(lineText, @"\s+", " ").Trim();

            var mCod = Regex.Match(text, @"C[OÓ]D\.\s*(\d+)");
            if (mCod.Success)
            {
                result.CodigoComprobante = mCod.Groups[1].Value;
                if (int.TryParse(result.CodigoComprobante, out var codigoInt) && CodigoALetra.TryGetValue(codigoInt, out var letra))
                    result.Letra = letra;
                else
                    result.Advertencias.Add($"Código de comprobante '{result.CodigoComprobante}' no está en la tabla de letras conocida; verificar la letra manualmente.");
            }

            if (Regex.IsMatch(text, @"NOTA\s+DE\s+CR[ÉE]DITO", RegexOptions.IgnoreCase))
                result.TipoComprobante = "NOTA DE CREDITO";
            else if (Regex.IsMatch(text, @"NOTA\s+DE\s+D[ÉE]BITO", RegexOptions.IgnoreCase))
                result.TipoComprobante = "NOTA DE DEBITO";
            else if (Regex.IsMatch(text, @"FACTURA", RegexOptions.IgnoreCase))
                result.TipoComprobante = "FACTURA";
            else
                result.Advertencias.Add("No se pudo determinar el tipo de comprobante (Factura/NC/ND).");

            var mPv = Regex.Match(text, @"Punto de Venta:\s*(?:Comp\.\s*Nro:\s*)?(\d{4,5})\D+(\d{6,8})");
            if (mPv.Success)
            {
                result.PuntoVenta = mPv.Groups[1].Value;
                result.Numero = mPv.Groups[2].Value;
            }
            else
            {
                result.Advertencias.Add("No se pudo extraer Punto de Venta / Número de comprobante.");
            }

            var mFechaEmision = Regex.Match(text, @"Fecha de Emisi[oó]n:\s*(\d{2}/\d{2}/\d{4})");
            result.FechaEmision = ParseArDate(mFechaEmision.Success ? mFechaEmision.Groups[1].Value : null);

            var mPeriodo = Regex.Match(text, @"Per[ií]odo Facturado Desde:\s*(\d{2}/\d{2}/\d{4})\s*Hasta:\s*(\d{2}/\d{2}/\d{4})");
            if (mPeriodo.Success)
            {
                result.PeriodoFacturadoDesde = ParseArDate(mPeriodo.Groups[1].Value);
                result.PeriodoFacturadoHasta = ParseArDate(mPeriodo.Groups[2].Value);
            }

            var mVtoPago = Regex.Match(text, @"Fecha de Vto\. para el pago:\s*(\d{2}/\d{2}/\d{4})");
            result.FechaVencimientoPago = ParseArDate(mVtoPago.Success ? mVtoPago.Groups[1].Value : null);

            var mCondVenta = Regex.Match(text, @"Condici[oó]n de venta:\s*(.+?)\s*(?:Precio Unit\.|C[oó]digo Producto|Opci[oó]n de Transferencia|$)");
            if (mCondVenta.Success) result.CondicionVenta = mCondVenta.Groups[1].Value.Trim();

            // ---- Partir el texto en secciones: emisor | receptor | items+totales ----
            int idxReceptor = Regex.Match(text, @"Apellido y Nombre\s*/\s*Raz[oó]n Social:").Index;
            if (idxReceptor <= 0)
            {
                result.Advertencias.Add("No se encontró el bloque del receptor (Apellido y Nombre / Razón Social). El documento puede no seguir el formato estándar ARCA/AFIP esperado.");
                idxReceptor = text.Length;
            }
            string emisorSection = text.Substring(0, idxReceptor);

            var mTablaIdx = Regex.Match(text, @"C[oó]digo Producto\s*/\s*Servicio");
            int idxTabla = mTablaIdx.Success ? mTablaIdx.Index : -1;
            string receptorSection = idxTabla > idxReceptor ? text.Substring(idxReceptor, idxTabla - idxReceptor) : text.Substring(idxReceptor);
            string itemsSection = idxTabla >= 0 ? text.Substring(idxTabla) : "";

            // Límite compartido para todas las capturas de esta zona: la tabla es de 2 columnas
            // (emisor/receptor) y, según el PDF, un campo puede terminar compartiendo la misma
            // línea "visual" que el campo siguiente en cualquier orden (izquierda/derecha). Por
            // eso cada captura corta en CUALQUIER etiqueta conocida, no solo en la esperada.
            const string Boundary = @"(?=CUIT:|Raz[oó]n Social:|Apellido y Nombre|Domicilio|Ingresos Brutos:|Fecha de Inicio de Actividades:|Fecha de Emisi[oó]n:|Condici[oó]n frente al IVA:|Per[ií]odo Facturado|Fecha de Vto\. para el pago:|Condici[oó]n de venta:|CBU del Emisor:|$)";

            // ---- CUIT emisor / receptor ----
            // El orden visual entre la etiqueta "CUIT:" y la razón social varía según la columna
            // en la que caiga cada uno (no siempre "Razón Social ... CUIT", a veces al revés), así
            // que en vez de asumir un orden fijo se empareja cada CUIT con la etiqueta de nombre
            // más cercana en el texto.
            var mApellido = Regex.Match(text, @"Apellido y Nombre\s*/\s*Raz[oó]n Social:");
            var mRazonSocialEmisor = Regex.Match(emisorSection, @"(?<!Apellido y Nombre\s*/\s*)Raz[oó]n Social:");
            var todosLosCuit = Regex.Matches(text, @"CUIT:\s*(\d{11})").Cast<Match>().ToList();

            Match? cuitReceptor = null, cuitEmisor = null;
            if (todosLosCuit.Count > 0 && mApellido.Success)
                cuitReceptor = todosLosCuit.OrderBy(m => Math.Abs(m.Index - mApellido.Index)).First();
            if (todosLosCuit.Count > 0 && mRazonSocialEmisor.Success)
                cuitEmisor = todosLosCuit.Where(m => m != cuitReceptor).OrderBy(m => Math.Abs(m.Index - mRazonSocialEmisor.Index)).FirstOrDefault()
                    ?? todosLosCuit.FirstOrDefault(m => m != cuitReceptor);

            if (cuitEmisor != null) result.ProveedorCuit = cuitEmisor.Groups[1].Value;
            if (cuitReceptor != null) result.ReceptorCuit = cuitReceptor.Groups[1].Value;

            // ---- Emisor (proveedor) ----
            var mEmisorRazon = Regex.Match(emisorSection, @"(?<!Apellido y Nombre\s*/\s*)Raz[oó]n Social:\s*(.+?)\s*" + Boundary);
            if (mEmisorRazon.Success) result.ProveedorRazonSocial = mEmisorRazon.Groups[1].Value.Trim();
            else result.Advertencias.Add("No se pudo extraer la Razón Social del emisor (proveedor).");

            if (cuitEmisor == null) result.Advertencias.Add("No se pudo extraer el CUIT del emisor (proveedor).");

            var mEmisorDom = Regex.Match(emisorSection, @"Domicilio Comercial:\s*(.+?)\s*" + Boundary);
            if (mEmisorDom.Success) result.ProveedorDomicilio = mEmisorDom.Groups[1].Value.Trim();

            var mEmisorIIBB = Regex.Match(emisorSection, @"Ingresos Brutos:\s*(.+?)\s*" + Boundary);
            if (mEmisorIIBB.Success) result.ProveedorIngresosBrutos = mEmisorIIBB.Groups[1].Value.Trim();

            var mEmisorCondIva = Regex.Match(emisorSection, @"Condici[oó]n frente al IVA:\s*(.+?)\s*" + Boundary);
            if (mEmisorCondIva.Success) result.ProveedorCondicionIva = mEmisorCondIva.Groups[1].Value.Trim();

            // ---- Receptor ----
            var mReceptorRazon = Regex.Match(receptorSection, @"Apellido y Nombre\s*/\s*Raz[oó]n Social:\s*(.+?)\s*" + Boundary);
            if (mReceptorRazon.Success) result.ReceptorRazonSocial = mReceptorRazon.Groups[1].Value.Trim();
            else result.Advertencias.Add("No se pudo extraer la Razón Social del receptor.");

            if (cuitReceptor == null) result.Advertencias.Add("No se pudo extraer el CUIT del receptor.");

            string cuitPropio = Regex.Replace(empresaCuit ?? "", @"\D", "");
            if (!string.IsNullOrEmpty(result.ReceptorCuit) && result.ReceptorCuit == cuitPropio)
            {
                result.EsCompra = true;
            }
            else if (!string.IsNullOrEmpty(result.ProveedorCuit) && result.ProveedorCuit == cuitPropio)
            {
                result.EsCompra = false;
                result.Advertencias.Add("El CUIT emisor coincide con el de la propia empresa: este comprobante es una VENTA, no una compra.");
            }
            else
            {
                result.Advertencias.Add("Ninguno de los CUIT detectados coincide con el CUIT configurado para este cliente. Verificar manualmente si corresponde registrar como compra.");
            }

            // ---- Totales ----
            decimal GetImporte(string label, string src)
            {
                var m = Regex.Match(src, Regex.Escape(label) + @":\s*\$?\s*(" + NUM + ")");
                return m.Success ? ParseArNumber(m.Groups[1].Value) : 0m;
            }

            result.Subtotal = GetImporte("Subtotal", itemsSection);
            result.ImporteNetoGravado = GetImporte("Importe Neto Gravado", itemsSection);

            void AgregarIva(string id, string label)
            {
                var importe = GetImporte(label, itemsSection);
                if (importe != 0) result.Ivas.Add(new ImporteConId { Id = id, Importe = importe });
            }
            AgregarIva("IVA27", "IVA 27%");
            AgregarIva("IVA21", "IVA 21%");
            AgregarIva("IVA105", "IVA 10.5%");
            AgregarIva("IVA5", "IVA 5%");
            AgregarIva("IVA2_5", "IVA 2.5%");
            AgregarIva("IVA0", "IVA 0%");

            var otrosTributos = GetImporte("Importe Otros Tributos", itemsSection);
            if (otrosTributos != 0) result.OtrosTributos.Add(new ImporteConId { Id = "OtrosTributos", Importe = otrosTributos });

            result.ImporteTotal = GetImporte("Importe Total", itemsSection);

            var mCae = Regex.Match(itemsSection, @"CAE N[°º]:\s*(\d+)");
            if (mCae.Success) result.Cae = mCae.Groups[1].Value;
            else result.Advertencias.Add("No se pudo extraer el CAE del comprobante.");

            var mVtoCae = Regex.Match(itemsSection, @"Fecha de Vto\. de CAE:\s*(\d{2}/\d{2}/\d{4})");
            result.FechaVtoCae = ParseArDate(mVtoCae.Success ? mVtoCae.Groups[1].Value : null);

            // ---- Items (por columnas reales, no por regex sobre texto aplanado) ----
            ExtraerItems(words, result);

            return result;
        }

        static void ExtraerItems(List<Word> words, Factura result)
        {
            var lines = PdfPigExtraction.GroupIntoLines(words, 3.0);

            var headerLine = lines.FirstOrDefault(l => l.Any(w =>
                w.Text.Equals("Código", StringComparison.OrdinalIgnoreCase) || w.Text.Equals("Codigo", StringComparison.OrdinalIgnoreCase)));
            var totalsLine = lines.FirstOrDefault(l => l.Any(w => w.Text.Equals("Subtotal:", StringComparison.OrdinalIgnoreCase)))
                ?? lines.FirstOrDefault(l => l.Any(w => w.Text.StartsWith("Importe", StringComparison.OrdinalIgnoreCase)));

            if (headerLine == null || totalsLine == null)
            {
                result.Advertencias.Add("No se pudo detectar ningún ítem de detalle en la tabla del comprobante.");
                return;
            }

            double tablaTop = headerLine[0].BoundingBox.Top;
            double tablaBottom = totalsLine[0].BoundingBox.Top;

            var filas = PdfPigExtraction.ExtractItemRows(words, tablaTop, tablaBottom);
            if (filas.Count == 0)
            {
                result.Advertencias.Add("No se pudo detectar ningún ítem de detalle en la tabla del comprobante.");
                return;
            }

            foreach (var fila in filas)
            {
                string desc = fila.Descripcion;
                string codigo = "";
                var mCodigo = Regex.Match(desc, @"^(\d{1,6})\s+(.*)$");
                if (mCodigo.Success)
                {
                    codigo = mCodigo.Groups[1].Value;
                    desc = mCodigo.Groups[2].Value.Trim();
                }

                string subtotal0 = fila.Columnas.GetValueOrDefault("Subtotal0", "");
                string alicuota = fila.Columnas.GetValueOrDefault("Alicuota", "");

                // Caso límite: si la etiqueta "Alicuota" quedó desalineada de su valor real en la
                // fila (encabezado angosto tipo "Alicuota IVA" partido en 2 líneas), el "21%" puede
                // terminar pegado al final del valor de Subtotal0 en vez de en su propia columna.
                var mAlicuotaEmbebida = Regex.Match(subtotal0, @"^(" + NUM + @")\s+(\d{1,2}(?:[.,]\d+)?)%$");
                if (string.IsNullOrEmpty(alicuota) && mAlicuotaEmbebida.Success)
                {
                    alicuota = mAlicuotaEmbebida.Groups[2].Value;
                    subtotal0 = mAlicuotaEmbebida.Groups[1].Value;
                }

                var detalle = new DetalleFactura
                {
                    Codigo = codigo,
                    Concepto = desc,
                    Cantidad = ParseArNumberOrZero(fila.Columnas.GetValueOrDefault("Cantidad")),
                    UnidadMedida = fila.Columnas.GetValueOrDefault("UM", "").Trim(),
                    PrecioUnitario = ParseArNumberOrZero(fila.Columnas.GetValueOrDefault("Precio")),
                    PorcentajeBonificacion = ParseArNumberOrZero(fila.Columnas.GetValueOrDefault("BonifPct")),
                    ImporteBonificacion = ParseArNumberOrZero(fila.Columnas.GetValueOrDefault("ImpBonif")),
                    Subtotal = ParseArNumberOrZero(subtotal0),
                    AlicuotaIva = string.IsNullOrEmpty(alicuota) ? 0m : ParseArNumber(alicuota.Replace(".", ",")),
                    SubtotalConIva = ParseArNumberOrZero(fila.Columnas.GetValueOrDefault("Subtotal1")),
                };

                result.Detalle.Add(detalle);
            }
        }
    }
}
