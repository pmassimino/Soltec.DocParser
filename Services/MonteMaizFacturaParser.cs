using System.Globalization;
using System.Text.RegularExpressions;
using Soltec.DocParser.Models;
using UglyToad.PdfPig.Content;

namespace Soltec.DocParser.Services
{
    // Parser para el formato de facturación propio de "Cooperativa Agrícola de Monte Maíz
    // Ltda." (no es el formato estándar ARCA/AFIP: usa sus propias etiquetas - "Cliente" en vez
    // de "Apellido y Nombre / Razón Social", "C.U.I.T" en vez de "CUIT:", tabla de ítems con
    // columnas "Código Cantidad Detalle Remito N° Precio Importe", etc.).
    //
    // Hay 2 variantes según la antigüedad del comprobante:
    //  - Variante "nueva" (la mayoría): un ítem por factura, encabezado de tabla en una sola
    //    línea. Totalmente soportada.
    //  - Variante "vieja" ("Señor/es" / "CUIT N°" / "Tipo 1" en vez de "Cod. NN"): permite
    //    varios ítems por factura con IVA distinto por línea, en un layout más difícil de
    //    reconstruir por columnas. Se reconoce el encabezado y el CAE, pero el detalle de
    //    ítems no se intenta parsear todavía -se avisa en vez de arriesgar números mal leídos-.
    public static class MonteMaizFacturaParser
    {
        // A diferencia del formato ARCA (que nunca usa separador de miles y "," es el decimal),
        // este proveedor usa la convención "internacional": "," para miles (opcional) y "." para
        // decimales -p.ej. "1,700.826" o "306148.68"-, así que el separador decimal siempre es el punto.
        const string NUM = @"[\d,]*\d\.\d+";

        static readonly Dictionary<int, string> CodigoALetra = new()
        {
            [1] = "A", [6] = "B", [11] = "C", [51] = "M",
        };

        static decimal ParseArNumber(string s) => decimal.Parse(s.Trim().Replace(",", ""), CultureInfo.InvariantCulture);

        static decimal ParseArNumberOrZero(string? s)
        {
            if (string.IsNullOrWhiteSpace(s)) return 0m;
            var m = Regex.Match(s, NUM);
            return m.Success ? ParseArNumber(m.Value) : 0m;
        }

        static DateTime? ParseArDate(string? s)
        {
            if (string.IsNullOrWhiteSpace(s)) return null;
            return DateTime.TryParseExact(s.Trim(), "dd/MM/yyyy", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? d : null;
        }

        public static bool EsFormatoMonteMaiz(string lineText)
        {
            return Regex.IsMatch(lineText, @"Monte Ma[ií]z", RegexOptions.IgnoreCase)
                && Regex.IsMatch(lineText, @"CUIT\s*:?\s*30-?53331931-?5");
        }

        public static FacturaCompra Parse(string lineText, List<Word> words, string empresaCuit)
        {
            var result = new FacturaCompra();
            string text = Regex.Replace(lineText, @"\s+", " ").Trim();

            bool esVariantePlana = Regex.IsMatch(text, @"\bCliente\b") && Regex.IsMatch(text, @"C\.U\.I\.T");
            bool esVarianteVieja = !esVariantePlana && Regex.IsMatch(text, @"Se[ñn]or/es");

            result.TipoComprobante = "FACTURA";

            var mCodigo = Regex.Match(text, @"Cod\.\s*(\d+)|Tipo\s*(\d+)");
            if (mCodigo.Success)
            {
                var codigoStr = mCodigo.Groups[1].Success ? mCodigo.Groups[1].Value : mCodigo.Groups[2].Value;
                result.CodigoComprobante = codigoStr;
                if (int.TryParse(codigoStr, out var codigoInt) && CodigoALetra.TryGetValue(codigoInt, out var letra))
                    result.Letra = letra;
            }
            // Además del código, la letra visible (A/B/C) está suelta en el encabezado; si no
            // se pudo derivar del código, se busca como respaldo.
            if (string.IsNullOrEmpty(result.Letra))
            {
                var mLetra = Regex.Match(text, @"\bFACTURA\b.*?\b(A|B|C|M)\b");
                if (mLetra.Success) result.Letra = mLetra.Groups[1].Value;
            }

            var mNumero = Regex.Match(text, @"(?:Numero|N[ºo]\.?)\s*(\d{4})[\s-]+(\d{5,8})");
            if (mNumero.Success)
            {
                result.PuntoVenta = mNumero.Groups[1].Value;
                result.Numero = mNumero.Groups[2].Value;
            }
            else
            {
                result.Advertencias.Add("No se pudo extraer Punto de Venta / Número de comprobante.");
            }

            var mFecha = Regex.Match(text, @"\bFecha:?\s*(\d{2}/\d{2}/\d{4})");
            result.FechaEmision = ParseArDate(mFecha.Success ? mFecha.Groups[1].Value : null);

            var mFechaVto = Regex.Match(text, @"Fecha Vto\.?\s*(\d{2}/\d{2}/\d{4})");
            result.FechaVencimientoPago = ParseArDate(mFechaVto.Success ? mFechaVto.Groups[1].Value : null);

            var mCondVenta = Regex.Match(text, @"Cond\.\s*Vent[ae]\.?\s*(.+?)\s*(?=Fecha Vto\.|C[oó]digo|CODIGO|$)");
            if (mCondVenta.Success) result.CondicionVenta = mCondVenta.Groups[1].Value.Trim();

            // ---- Emisor: siempre la propia cooperativa ("Monte Maíz"), con CUIT fijo ----
            var mEmisorCuit = Regex.Match(text, @"(?<!\.)(?<!N[°º]\s*)\bCUIT\s*:?\s*(30-?53331931-?5)");
            result.ProveedorCuit = mEmisorCuit.Success ? Regex.Replace(mEmisorCuit.Groups[1].Value, @"\D", "") : "";
            result.ProveedorRazonSocial = "Cooperativa Agrícola de Monte Maíz Ltda.";
            var mEmisorIIBB = Regex.Match(text, @"Ing\. Brutos:\s*(\d+)");
            if (mEmisorIIBB.Success) result.ProveedorIngresosBrutos = mEmisorIIBB.Groups[1].Value;

            // ---- Receptor (cliente) ----
            var mReceptorNombre = Regex.Match(text, @"(?:Se[ñn]or/es|Cliente)\s+(.+?)\s*(?=Cuenta|Domicilio|$)");
            if (mReceptorNombre.Success) result.ReceptorRazonSocial = mReceptorNombre.Groups[1].Value.Trim();
            else result.Advertencias.Add("No se pudo extraer el nombre del cliente (receptor).");

            var mReceptorCuit = Regex.Match(text, @"CUIT\s*N[°º]\s*(\d{11})|C\.U\.I\.T\s*(\d{11})");
            if (mReceptorCuit.Success)
                result.ReceptorCuit = mReceptorCuit.Groups[1].Success ? mReceptorCuit.Groups[1].Value : mReceptorCuit.Groups[2].Value;
            else
                result.Advertencias.Add("No se pudo extraer el CUIT del cliente (receptor).");

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

            // ---- CAE ----
            var mCae = Regex.Match(text, @"\bCAE\s*:?\s*(\d+)");
            if (mCae.Success) result.Cae = mCae.Groups[1].Value;
            else result.Advertencias.Add("No se pudo extraer el CAE del comprobante.");

            var mVtoCae = Regex.Match(text, @"Fecha Venc\.\s*Cae:?\s*(\d{2}/\d{2}/\d{4})");
            result.FechaVtoCae = ParseArDate(mVtoCae.Success ? mVtoCae.Groups[1].Value : null);

            // ---- Ítems y totales ----
            if (esVariantePlana)
            {
                ExtraerItemsYTotalesVarianteNueva(words, result);
            }
            else if (esVarianteVieja)
            {
                result.Advertencias.Add("Comprobante de la variante antigua de este proveedor (numeración '0019'): el detalle de ítems y los totales todavía no se parsean automáticamente para este formato. Cargar manualmente.");
            }
            else
            {
                result.Advertencias.Add("No se pudo determinar la variante del formato Monte Maíz para extraer ítems y totales.");
            }

            return result;
        }

        static void ExtraerItemsYTotalesVarianteNueva(List<Word> words, FacturaCompra result)
        {
            var lines = PdfPigExtraction.GroupIntoLines(words, 3.0);

            var headerLine = lines.FirstOrDefault(l => l.Any(w => w.Text.Equals("Código", StringComparison.OrdinalIgnoreCase))
                                                     && l.Any(w => w.Text.Equals("Detalle", StringComparison.OrdinalIgnoreCase)));
            if (headerLine == null)
            {
                result.Advertencias.Add("No se pudo detectar ningún ítem de detalle en la tabla del comprobante.");
                return;
            }

            var totalesLabelLine = lines.FirstOrDefault(l => l.Any(w => w.Text.Equals("Sub.", StringComparison.OrdinalIgnoreCase))
                                                            && l.Any(w => w.Text.Equals("Total", StringComparison.OrdinalIgnoreCase)));
            double tablaTop = headerLine[0].BoundingBox.Top;
            double tablaBottom = totalesLabelLine != null ? totalesLabelLine[0].BoundingBox.Top : double.MinValue;

            var enTabla = words.Where(w => w.BoundingBox.Top < tablaTop && w.BoundingBox.Top > tablaBottom).ToList();

            var headerAnchors = new List<(string label, double left)>();
            void AddAnchor(string label, Func<Word, bool> match)
            {
                var w = headerLine.FirstOrDefault(match);
                if (w != null) headerAnchors.Add((label, w.BoundingBox.Left));
            }
            AddAnchor("Codigo", w => w.Text.Equals("Código", StringComparison.OrdinalIgnoreCase));
            AddAnchor("Cantidad", w => w.Text.Equals("Cantidad", StringComparison.OrdinalIgnoreCase));
            AddAnchor("Detalle", w => w.Text.Equals("Detalle", StringComparison.OrdinalIgnoreCase));
            AddAnchor("Remito", w => w.Text.Equals("Remito", StringComparison.OrdinalIgnoreCase));
            AddAnchor("Precio", w => w.Text.Equals("Precio", StringComparison.OrdinalIgnoreCase));
            AddAnchor("Importe", w => w.Text.Equals("Importe", StringComparison.OrdinalIgnoreCase));
            headerAnchors = headerAnchors.OrderBy(a => a.left).ToList();

            if (headerAnchors.Count == 0)
            {
                result.Advertencias.Add("No se pudo detectar ningún ítem de detalle en la tabla del comprobante.");
                return;
            }

            // A diferencia del formato ARCA, en este las etiquetas de encabezado no quedan
            // alineadas al borde izquierdo real de los valores de la columna (p.ej. "Detalle"
            // aparece más centrada, y el texto real -"GAS OIL"- empieza antes que la etiqueta).
            // Por eso el límite entre columnas se define a mitad de camino entre etiquetas
            // consecutivas en vez de en el borde exacto de cada una.
            var columns = new List<TableColumn>();
            for (int i = 0; i < headerAnchors.Count; i++)
            {
                double left = i == 0 ? double.MinValue : (headerAnchors[i - 1].left + headerAnchors[i].left) / 2.0;
                double right = i + 1 < headerAnchors.Count ? (headerAnchors[i].left + headerAnchors[i + 1].left) / 2.0 : double.MaxValue;
                columns.Add(new TableColumn { Name = headerAnchors[i].label, Left = left, Right = right });
            }
            string ColumnFor(double x) => columns.FirstOrDefault(c => x >= c.Left && x < c.Right)?.Name ?? "?";

            // Exige que la columna "Cantidad" tenga un número decimal completo (no solo texto
            // que arranca con un dígito), porque una descripción envuelta en 2 líneas puede
            // dejar un número suelto (p.ej. de un número de referencia) en esa franja de X.
            var itemLines = PdfPigExtraction.GroupIntoLines(enTabla, 3.0)
                .Where(l => l.Any(w => ColumnFor(w.BoundingBox.Left) == "Cantidad" && Regex.IsMatch(w.Text, "^" + NUM + "$")))
                .ToList();

            if (itemLines.Count == 0)
            {
                result.Advertencias.Add("No se pudo detectar ningún ítem de detalle en la tabla del comprobante.");
            }

            foreach (var linea in itemLines)
            {
                var porColumna = linea.GroupBy(w => ColumnFor(w.BoundingBox.Left))
                    .ToDictionary(g => g.Key, g => string.Join(" ", g.OrderBy(w => w.BoundingBox.Left).Select(w => w.Text)));

                var detalle = new DetalleFacturaCompra
                {
                    Codigo = porColumna.GetValueOrDefault("Codigo", "").Trim(),
                    Concepto = porColumna.GetValueOrDefault("Detalle", "").Trim(),
                    Cantidad = ParseArNumberOrZero(porColumna.GetValueOrDefault("Cantidad")),
                    PrecioUnitario = ParseArNumberOrZero(porColumna.GetValueOrDefault("Precio")),
                    Subtotal = ParseArNumberOrZero(porColumna.GetValueOrDefault("Importe")),
                };

                // Salvaguarda: si a pesar del filtro anterior la fila no tiene ni cantidad ni
                // importe, es un renglón fantasma (resto de una descripción envuelta), no un ítem.
                if (detalle.Cantidad == 0 && detalle.Subtotal == 0) continue;

                result.Detalle.Add(detalle);
            }

            // Totales: no hay adyacencia textual entre cada etiqueta y su valor (viven en dos
            // líneas separadas, alineadas por columna) y la cantidad de columnas intermedias
            // varía según el comprobante (Perc./N.G/Iva. sólo aparecen si tienen importe), así
            // que en vez de mapear cada columna se toma el primer valor de la fila como el
            // subtotal neto y el último como el total final -son las dos columnas fijas que
            // siempre están, en ese orden, en todos los comprobantes vistos de este proveedor-.
            if (totalesLabelLine != null)
            {
                var valoresLine = PdfPigExtraction.GroupIntoLines(words, 3.0)
                    .FirstOrDefault(l => l[0].BoundingBox.Top < totalesLabelLine[0].BoundingBox.Top
                                      && l.Count(w => Regex.IsMatch(w.Text, @"^" + NUM + "$")) >= 2);
                if (valoresLine != null)
                {
                    var valores = valoresLine.Where(w => Regex.IsMatch(w.Text, @"^" + NUM + "$"))
                        .OrderBy(w => w.BoundingBox.Left).Select(w => w.Text).ToList();
                    if (valores.Count >= 2)
                    {
                        result.Subtotal = ParseArNumberOrZero(valores.First());
                        result.ImporteTotal = ParseArNumberOrZero(valores.Last());
                        result.ImporteNetoGravado = result.Subtotal;
                        if (result.ImporteTotal != result.Subtotal)
                            result.Advertencias.Add($"El IVA no se discrimina por alícuota en este formato; el total incluye ${result.ImporteTotal - result.Subtotal:F2} de impuestos/percepciones sin desglosar.");
                    }
                }
                else
                {
                    result.Advertencias.Add("No se pudieron extraer los totales del comprobante.");
                }
            }
        }
    }
}
