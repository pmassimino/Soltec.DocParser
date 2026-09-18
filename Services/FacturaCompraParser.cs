using System.Globalization;
using System.Text.RegularExpressions;
using Soltec.DocParser.Models;

namespace Soltec.DocParser.Services
{
    // Parser por reglas para el formato estándar de comprobante electrónico ARCA/AFIP
    // ("Factura A/B/C", "Factura de Crédito Electrónica MiPyMEs", etc. emitidos desde el
    // portal de comprobantes en línea). No cubre formatos de facturación propios de cada
    // proveedor (ver Advertencias del resultado).
    public static class FacturaCompraParser
    {
        // Los importes pueden venir con separador de miles ("1.971.200,00") o sin él ("1971200,00")
        const string NUM = @"(?:\d{1,3}(?:\.\d{3})+|\d+),\d{2}";

        static decimal ParseArNumber(string s)
        {
            s = s.Trim().Replace(".", "").Replace(",", ".");
            return decimal.Parse(s, CultureInfo.InvariantCulture);
        }

        static DateTime? ParseArDate(string? s)
        {
            if (string.IsNullOrWhiteSpace(s)) return null;
            return DateTime.TryParseExact(s.Trim(), "dd/MM/yyyy", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d)
                ? d
                : null;
        }

        public static FacturaCompra Parse(string rawText, string empresaCuit)
        {
            var result = new FacturaCompra();

            // Normalizar a una sola línea: la extracción de texto del PDF no respeta el orden
            // visual de lectura, pero las etiquetas y sus valores quedan casi siempre adyacentes.
            string text = Regex.Replace(rawText, @"\s+", " ").Trim();

            var mCod = Regex.Match(text, @"C[OÓ]D\.\s*(\d+)");
            if (mCod.Success) result.CodigoComprobante = mCod.Groups[1].Value;

            if (Regex.IsMatch(text, @"NOTA\s+DE\s+CR[ÉE]DITO", RegexOptions.IgnoreCase))
                result.TipoComprobante = "NOTA DE CREDITO";
            else if (Regex.IsMatch(text, @"NOTA\s+DE\s+D[ÉE]BITO", RegexOptions.IgnoreCase))
                result.TipoComprobante = "NOTA DE DEBITO";
            else if (Regex.IsMatch(text, @"FACTURA", RegexOptions.IgnoreCase))
                result.TipoComprobante = "FACTURA";
            else
                result.Advertencias.Add("No se pudo determinar el tipo de comprobante (Factura/NC/ND).");

            var mLetra = Regex.Match(text, @"\b(A|B|C|M)\b\s*C[OÓ]D\.\s*\d+");
            if (mLetra.Success) result.Letra = mLetra.Groups[1].Value;

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

            var mCondVenta = Regex.Match(text, @"Condici[oó]n de venta:\s*(.+?)\s*(?:Precio Unit\.|C[oó]digo Producto|$)");
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

            // ---- Emisor (proveedor) ----
            var mEmisorCuit = Regex.Match(emisorSection, @"(?<!Apellido y Nombre\s*/\s*)Raz[oó]n Social:\s*(.+?)\s*CUIT:\s*(\d{11})");
            if (mEmisorCuit.Success)
            {
                result.ProveedorRazonSocial = mEmisorCuit.Groups[1].Value.Trim();
                result.ProveedorCuit = mEmisorCuit.Groups[2].Value;
            }
            else
            {
                result.Advertencias.Add("No se pudo extraer Razón Social / CUIT del emisor (proveedor).");
            }

            var mEmisorDom = Regex.Match(emisorSection, @"Domicilio Comercial:\s*(.+?)\s*Ingresos Brutos:");
            if (mEmisorDom.Success) result.ProveedorDomicilio = mEmisorDom.Groups[1].Value.Trim();

            var mEmisorIIBB = Regex.Match(emisorSection, @"Ingresos Brutos:\s*(.+?)\s*Fecha de Inicio de Actividades:");
            if (mEmisorIIBB.Success) result.ProveedorIngresosBrutos = mEmisorIIBB.Groups[1].Value.Trim();

            var mEmisorCondIva = Regex.Match(emisorSection, @"Condici[oó]n frente al IVA:\s*(.+?)\s*(?=Per[ií]odo Facturado|Fecha de Vto\. para el pago|CBU del Emisor|$)");
            if (mEmisorCondIva.Success) result.ProveedorCondicionIva = mEmisorCondIva.Groups[1].Value.Trim();

            // ---- Receptor ----
            // El orden "Razón Social ... CUIT" es el más común, pero algunas variantes (p.ej. FCE) lo invierten.
            var mReceptor = Regex.Match(receptorSection, @"Apellido y Nombre\s*/\s*Raz[oó]n Social:\s*(.+?)\s*CUIT:\s*(\d{11})");
            if (mReceptor.Success)
            {
                result.ReceptorRazonSocial = mReceptor.Groups[1].Value.Trim();
                result.ReceptorCuit = mReceptor.Groups[2].Value;
            }
            else
            {
                var mReceptorAlt = Regex.Match(receptorSection, @"CUIT:\s*(\d{11})\s*Apellido y Nombre\s*/\s*Raz[oó]n Social:\s*(.+?)\s*(?=Condici[oó]n frente al IVA|Domicilio|$)");
                if (mReceptorAlt.Success)
                {
                    result.ReceptorCuit = mReceptorAlt.Groups[1].Value;
                    result.ReceptorRazonSocial = mReceptorAlt.Groups[2].Value.Trim();
                }
                else
                {
                    result.Advertencias.Add("No se pudo extraer Razón Social / CUIT del receptor.");
                }
            }

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
            result.Iva27 = GetImporte("IVA 27%", itemsSection);
            result.Iva21 = GetImporte("IVA 21%", itemsSection);
            result.Iva105 = GetImporte("IVA 10.5%", itemsSection);
            result.Iva5 = GetImporte("IVA 5%", itemsSection);
            result.Iva25 = GetImporte("IVA 2.5%", itemsSection);
            result.Iva0 = GetImporte("IVA 0%", itemsSection);
            result.ImporteOtrosTributos = GetImporte("Importe Otros Tributos", itemsSection);
            result.ImporteTotal = GetImporte("Importe Total", itemsSection);

            var mCae = Regex.Match(itemsSection, @"CAE N[°º]:\s*(\d+)");
            if (mCae.Success) result.Cae = mCae.Groups[1].Value;
            else result.Advertencias.Add("No se pudo extraer el CAE del comprobante.");

            var mVtoCae = Regex.Match(itemsSection, @"Fecha de Vto\. de CAE:\s*(\d{2}/\d{2}/\d{4})");
            result.FechaVtoCae = ParseArDate(mVtoCae.Success ? mVtoCae.Groups[1].Value : null);

            // ---- Items ----
            // Recorta la sección de items a lo que hay ANTES de "Subtotal:" / "Importe..." (ahí empiezan los totales)
            int idxTotales = itemsSection.Length;
            foreach (var marker in new[] { "Subtotal:", "Importe Otros Tributos:", "Importe Neto Gravado:", "Importe Total:" })
            {
                var idx = itemsSection.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
                if (idx >= 0 && idx < idxTotales) idxTotales = idx;
            }
            string tablaTexto = itemsSection.Substring(0, idxTotales);

            // Un ítem = una línea con la secuencia numérica de cantidad/precio/etc.
            // Casos vistos: [codigo]? [desc]? cantidad [um]? precio bonif% [impbonif]? subtotal [alicuota% subtotalIva]?
            var itemRegex = new Regex(
                @"(?<pre>.*?)(?<cantidad>" + NUM + @")\s+(?:(?<um1>unidades|otras\s+unidades|kg|litros|lts)\s+)?" +
                @"(?<precio>" + NUM + @")\s+(?<bonifpct>" + NUM + @")\s+" +
                @"(?:(?<impbonif>" + NUM + @")\s+)?(?<subtotal>" + NUM + @")" +
                @"(?:\s+(?<alicuota>\d{1,2}(?:[.,]\d+)?)%\s+(?<subtotaliva>" + NUM + @"))?",
                RegexOptions.IgnoreCase);

            var matches = itemRegex.Matches(tablaTexto);
            if (matches.Count == 0)
            {
                result.Advertencias.Add("No se pudo detectar ningún ítem de detalle en la tabla del comprobante.");
            }

            var boundaries = matches.Select(m => (start: m.Index, end: m.Index + m.Length, m)).ToList();
            int lastEnd = 0;
            for (int i = 0; i < boundaries.Count; i++)
            {
                var (start, end, m) = boundaries[i];
                int nextStart = (i == boundaries.Count - 1) ? tablaTexto.Length : boundaries[i + 1].start;
                string before = tablaTexto.Substring(lastEnd, start - lastEnd);
                string after = tablaTexto.Substring(end, nextStart - end);
                lastEnd = nextStart;

                string pre = m.Groups["pre"].Value;
                // El texto de la descripción suele quedar partido antes y después de la fila numérica
                // (efecto de columnas del PDF), así que se reconstruye uniendo ambos lados.
                string desc = (before + " " + pre + " " + after).Trim();
                // Sacar boilerplate de encabezados de columnas / palabras de unidad que puedan quedar mezclados.
                // Nota: sin \b después de un ".", porque entre dos caracteres no-palabra (p.ej "." y " ") \b nunca matchea.
                desc = Regex.Replace(desc, @"C[oó]digo Producto\s*/\s*Servicio|\bCantidad\b|U\.\s*Medida|Precio Unit\.|%\s*Bonif|Imp\.\s*Bonif\.?|\bSubtotal\b|\bAlicuota\b|\bIVA\b|\botras\b|\bunidades\b", "", RegexOptions.IgnoreCase);
                desc = Regex.Replace(desc, @"\s+", " ").Trim();

                var detalle = new DetalleFacturaCompra
                {
                    Concepto = desc,
                    Cantidad = ParseArNumber(m.Groups["cantidad"].Value),
                    UnidadMedida = m.Groups["um1"].Success ? m.Groups["um1"].Value.Trim() : "",
                    PrecioUnitario = ParseArNumber(m.Groups["precio"].Value),
                    PorcentajeBonificacion = ParseArNumber(m.Groups["bonifpct"].Value),
                    ImporteBonificacion = m.Groups["impbonif"].Success ? ParseArNumber(m.Groups["impbonif"].Value) : 0m,
                    Subtotal = ParseArNumber(m.Groups["subtotal"].Value),
                    AlicuotaIva = m.Groups["alicuota"].Success ? ParseArNumber(m.Groups["alicuota"].Value.Replace(".", ",")) : 0m,
                    SubtotalConIva = m.Groups["subtotaliva"].Success ? ParseArNumber(m.Groups["subtotaliva"].Value) : 0m,
                };

                // intenta separar un código numérico inicial (ej: "001 COMISIÓN...")
                var mCodigo = Regex.Match(detalle.Concepto, @"^(\d{1,6})\s+(.*)$");
                if (mCodigo.Success)
                {
                    detalle.Codigo = mCodigo.Groups[1].Value;
                    detalle.Concepto = mCodigo.Groups[2].Value.Trim();
                }

                result.Detalle.Add(detalle);
            }

            return result;
        }
    }
}
