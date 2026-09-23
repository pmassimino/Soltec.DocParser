using System.Globalization;
using System.Text.RegularExpressions;
using Soltec.DocParser.Models;

namespace Soltec.DocParser.Services
{
    // Quinto formato de factura visto (primero en Atiseni S.A.S.), distinto de ARCA, SAE,
    // "Gravado/Exento" y "SUB TOTAL/TOTAL": encabezado "FACTURA VENTA", "Punto Venta"/"N°" sin
    // punto después de "Punto", "Cliente:" para el receptor, tabla de ítems "Código Artículo
    // Denominación Pcio.Unit. Cant. % D/R SubTotal", y totales en una sola fila de 5 columnas
    // "NETO IVA NO GRAVADO OTROS IMPUESTOS TOTAL" sin discriminar el IVA por alícuota.
    // El nombre del parser se basa en esa fila de totales, no en la empresa.
    public static class FacturaNetoParser
    {
        static readonly Dictionary<int, string> CodigoALetra = new()
        {
            [1] = "A", [6] = "B", [11] = "C", [51] = "M",
        };

        const string NUM = @"[\d.,]+";

        static decimal ParseNumeroOZero(string? s)
        {
            if (string.IsNullOrWhiteSpace(s)) return 0m;
            return decimal.TryParse(s.Trim().Replace(".", "").Replace(",", "."), NumberStyles.Any, CultureInfo.InvariantCulture, out var v) ? v : 0m;
        }

        static DateTime? ParseArDate(string? s)
        {
            if (string.IsNullOrWhiteSpace(s)) return null;
            return DateTime.TryParseExact(s.Trim(), "dd/MM/yyyy", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? d : null;
        }

        public static bool EsFormatoNeto(string lineText)
        {
            string text = Regex.Replace(lineText, @"\s+", " ");
            return Regex.IsMatch(text, @"\bNETO\b") && Regex.IsMatch(text, @"NO GRAVADO")
                && Regex.IsMatch(text, @"OTROS IMPUESTOS") && Regex.IsMatch(text, @"\bCliente:");
        }

        static void AgregarIva(Factura result, decimal importe)
        {
            if (importe == 0) return;
            // Este formato no discrimina por alícuota (una sola columna "IVA"); se asume la
            // alícuota general (21%, la estándar en Argentina) como en los demás formatos SAE
            // que tampoco discriminan.
            result.Ivas.Add(new ImporteConId { Id = "IVA21", Importe = importe });
        }

        public static Factura Parse(string lineText, List<PositionedWord> words, string empresaCuit)
        {
            var result = new Factura { TipoComprobante = "FACTURA" };
            var lineas = lineText.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).ToList();
            string text = Regex.Replace(lineText, @"\s+", " ").Trim();

            var mCodigo = Regex.Match(text, @"COD\.\s*(\d+)");
            if (mCodigo.Success)
            {
                result.CodigoComprobante = mCodigo.Groups[1].Value;
                if (int.TryParse(mCodigo.Groups[1].Value, out var num) && CodigoALetra.TryGetValue(num, out var letra))
                    result.Letra = letra;
            }

            var mNumero = Regex.Match(text, @"Punto Venta\s+(\d{4,5})\s+N[°º]\s*(\d{5,8})");
            if (mNumero.Success)
            {
                result.PuntoVenta = mNumero.Groups[1].Value;
                result.Numero = mNumero.Groups[2].Value;
            }
            else
            {
                result.Advertencias.Add("No se pudo extraer Punto de Venta / Número de comprobante.");
            }

            var mFecha = Regex.Match(text, @"Fecha Emisi[oó]n:\s*(\d{2}/\d{2}/\d{4})");
            result.FechaEmision = ParseArDate(mFecha.Success ? mFecha.Groups[1].Value : null);

            var mCondVenta = Regex.Match(text, @"Condici[oó]n Venta:\s*(.+?)\s*(?=Vendedor:|$)");
            if (mCondVenta.Success) result.CondicionVenta = mCondVenta.Groups[1].Value.Trim();

            // ---- Emisor ----
            // El nombre no tiene etiqueta: es la línea siguiente a "COD. NN" (posicional, más
            // frágil que el resto).
            int idxCod = lineas.FindIndex(l => Regex.IsMatch(l, @"^COD\.\s*\d+"));
            if (idxCod >= 0 && idxCod + 1 < lineas.Count)
                result.ProveedorRazonSocial = lineas[idxCod + 1].Trim();
            else
                result.Advertencias.Add("No se pudo extraer la Razón Social del emisor (posicional, sin etiqueta).");

            // "CUIT:" y "C.U.I.T.:" (con o sin puntos) aparecen las dos para el emisor con el
            // mismo valor; se toma la primera ocurrencia de cualquiera de las dos, antes de "Cliente:".
            int idxCliente = text.IndexOf("Cliente:", StringComparison.Ordinal);
            string emisorSection = idxCliente > 0 ? text.Substring(0, idxCliente) : text;
            var mEmisorCuit = Regex.Match(emisorSection, @"C\.?U\.?I\.?T\.?:\s*([\d-]{11,13})");
            if (mEmisorCuit.Success) result.ProveedorCuit = Regex.Replace(mEmisorCuit.Groups[1].Value, @"\D", "");
            else result.Advertencias.Add("No se pudo extraer el CUIT del emisor.");

            // ---- Receptor ----
            var mReceptorNombre = Regex.Match(text, @"Cliente:\s*(.+?)\s*Domicilio:");
            if (mReceptorNombre.Success) result.ReceptorRazonSocial = mReceptorNombre.Groups[1].Value.Trim();
            else result.Advertencias.Add("No se pudo extraer el nombre del cliente (receptor).");

            var mReceptorCuit = Regex.Match(text, @"N[°º]\s*CUIT:\s*([\d-]{11,13})");
            if (mReceptorCuit.Success) result.ReceptorCuit = Regex.Replace(mReceptorCuit.Groups[1].Value, @"\D", "");
            else result.Advertencias.Add("No se pudo extraer el CUIT del cliente (receptor).");

            // La decisión de si es compra o venta se toma centralizada, después de aplicar el QR
            // (ver QrReconciliation.DecidirEsCompra).

            // ---- CAE ----
            var mCae = Regex.Match(text, @"CAE N[°º]:\s*(\d+)");
            if (mCae.Success) result.Cae = mCae.Groups[1].Value;
            else result.Advertencias.Add("No se pudo extraer el CAE del comprobante.");

            var mVtoCae = Regex.Match(text, @"Fecha Vto\. de CAE:\s*(\d{2}/\d{2}/\d{4})");
            result.FechaVtoCae = ParseArDate(mVtoCae.Success ? mVtoCae.Groups[1].Value : null);

            // ---- Totales: una fila de etiquetas ("NETO IVA NO GRAVADO OTROS IMPUESTOS TOTAL")
            // seguida de una fila de 5 valores, en ese mismo orden. ----
            int idxTotalesLabel = lineas.FindIndex(l => Regex.IsMatch(l, @"^NETO\s+IVA\s+NO GRAVADO"));
            if (idxTotalesLabel >= 0 && idxTotalesLabel + 1 < lineas.Count)
            {
                var valores = Regex.Matches(lineas[idxTotalesLabel + 1], NUM).Select(m => m.Value).ToList();
                if (valores.Count == 5)
                {
                    result.Subtotal = result.ImporteNetoGravado = ParseNumeroOZero(valores[0]);
                    AgregarIva(result, ParseNumeroOZero(valores[1]));
                    if (ParseNumeroOZero(valores[2]) != 0) result.OtrosTributos.Add(new ImporteConId { Id = "NoGravado", Importe = ParseNumeroOZero(valores[2]) });
                    if (ParseNumeroOZero(valores[3]) != 0) result.OtrosTributos.Add(new ImporteConId { Id = "OtrosImpuestos", Importe = ParseNumeroOZero(valores[3]) });
                    result.ImporteTotal = ParseNumeroOZero(valores[4]);
                }
                else
                {
                    result.Advertencias.Add("No se pudo relacionar cada columna de totales con su valor (formato no previsto).");
                }
            }
            else
            {
                result.Advertencias.Add("No se pudieron extraer los totales del comprobante.");
            }

            // ---- Ítems ----
            // Cada ítem vive en una sola línea (CODIGO DENOMINACION PRECIO CANTIDAD %D/R SUBTOTAL).
            var itemRegex = new Regex(@"^(?<codigo>\d+)\s+(?<desc>.+?)\s+(?<precio>" + NUM + @")\s+(?<cant>" + NUM + @")\s+(?<dr>" + NUM + @")\s+(?<subtotal>" + NUM + @")\s*$");
            bool enTabla = false;
            foreach (var linea in lineas)
            {
                if (Regex.IsMatch(linea, @"^C[oó]digo\s+Art[ií]culo")) { enTabla = true; continue; }
                if (!enTabla) continue;
                if (Regex.IsMatch(linea, @"^NETO\s+IVA")) break;

                var m = itemRegex.Match(linea);
                if (!m.Success) continue;

                result.Detalle.Add(new DetalleFactura
                {
                    Codigo = m.Groups["codigo"].Value,
                    Concepto = m.Groups["desc"].Value.Trim(),
                    Cantidad = ParseNumeroOZero(m.Groups["cant"].Value),
                    PrecioUnitario = ParseNumeroOZero(m.Groups["precio"].Value),
                    PorcentajeBonificacion = ParseNumeroOZero(m.Groups["dr"].Value),
                    Subtotal = ParseNumeroOZero(m.Groups["subtotal"].Value),
                });
            }

            if (result.Detalle.Count == 0)
                result.Advertencias.Add("No se pudo detectar ningún ítem de detalle en la tabla del comprobante.");

            return result;
        }
    }
}
