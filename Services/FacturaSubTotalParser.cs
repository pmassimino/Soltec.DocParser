using System.Globalization;
using System.Text.RegularExpressions;
using Soltec.DocParser.Models;

namespace Soltec.DocParser.Services
{
    // Cuarto formato de factura visto (primero en APSA Internacional S.A.), distinto de ARCA,
    // SAE y "Gravado/Exento": etiquetas propias ("C.U.I.T.:" con puntos para el emisor, "CUIT:"
    // sin puntos para el receptor -al revés que en los otros formatos-, sin ninguna etiqueta tipo
    // "Cliente"/"Sres:" antes del nombre del receptor), fechas con puntos ("20.08.2026") o en
    // texto ("19 DE SEPTIEMBRE DE 2026") en vez de con barras, tabla "CODIGO CONCEPTO CANTIDAD N
    // LOTE DESPACHO PCIO.UNIT IMPORTE" con precio unitario en moneda extranjera, y totales
    // simples "SUB TOTAL $ / IVA X% / TOTAL $" sin discriminar Gravado/Exento/Percepciones.
    // El nombre del parser se basa en esa estructura de totales, no en la empresa.
    //
    // Solo se validó contra un comprobante real con un único ítem; la Razón Social del emisor y
    // del receptor se ubican por posición dentro del documento (no hay ninguna etiqueta que las
    // ancle), así que son más frágiles que el resto de los campos -ver advertencias si no dan bien-.
    public static class FacturaSubTotalParser
    {
        static readonly Dictionary<int, string> CodigoALetra = new()
        {
            [1] = "A", [6] = "B", [11] = "C", [51] = "M",
        };

        static readonly Dictionary<string, int> Meses = new(StringComparer.OrdinalIgnoreCase)
        {
            ["ENERO"] = 1, ["FEBRERO"] = 2, ["MARZO"] = 3, ["ABRIL"] = 4, ["MAYO"] = 5, ["JUNIO"] = 6,
            ["JULIO"] = 7, ["AGOSTO"] = 8, ["SEPTIEMBRE"] = 9, ["SETIEMBRE"] = 9, ["OCTUBRE"] = 10,
            ["NOVIEMBRE"] = 11, ["DICIEMBRE"] = 12,
        };

        const string NUM = @"[\d.,]+";

        static decimal ParseNumeroOZero(string? s)
        {
            if (string.IsNullOrWhiteSpace(s)) return 0m;
            return decimal.TryParse(s.Trim().Replace(".", "").Replace(",", "."), NumberStyles.Any, CultureInfo.InvariantCulture, out var v) ? v : 0m;
        }

        static DateTime? ParseFechaPuntos(string? s)
        {
            var m = Regex.Match(s ?? "", @"(\d{2})\.(\d{2})\.(\d{4})");
            return m.Success && DateTime.TryParse($"{m.Groups[3]}-{m.Groups[2]}-{m.Groups[1]}", out var d) ? d : null;
        }

        static DateTime? ParseFechaLarga(string? s)
        {
            var m = Regex.Match(s ?? "", @"(\d{1,2})\s+DE\s+(\w+)\s+DE\s+(\d{4})", RegexOptions.IgnoreCase);
            if (!m.Success || !Meses.TryGetValue(m.Groups[2].Value, out var mes)) return null;
            return new DateTime(int.Parse(m.Groups[3].Value), mes, int.Parse(m.Groups[1].Value));
        }

        public static bool EsFormatoSubTotal(string lineText)
        {
            return Regex.IsMatch(lineText, @"SUB TOTAL\s*\$") && Regex.IsMatch(lineText, @"C\.U\.I\.T\.")
                && Regex.IsMatch(lineText, @"\bFACTURA\b") && Regex.IsMatch(lineText, @"\bCAE\b");
        }

        static void AgregarIva(Factura result, string etiqueta, decimal importe)
        {
            if (importe == 0) return;
            string id = etiqueta.Trim().Replace(",", ".") switch
            {
                "27" or "27.00" => "IVA27",
                "21" or "21.00" => "IVA21",
                "10.5" or "10.50" => "IVA105",
                "5" or "5.00" => "IVA5",
                "2.5" or "2.50" => "IVA25",
                "0" or "0.00" => "IVA0",
                var otra => "IVA_" + Regex.Replace(otra, @"[^\w]", ""),
            };
            var existente = result.Ivas.FirstOrDefault(i => i.Id == id);
            if (existente != null) existente.Importe += importe;
            else result.Ivas.Add(new ImporteConId { Id = id, Importe = importe });
        }

        public static Factura Parse(string lineText, List<PositionedWord> words, string empresaCuit)
        {
            var result = new Factura { TipoComprobante = "FACTURA" };
            var lineas = lineText.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).ToList();
            string text = Regex.Replace(lineText, @"\s+", " ").Trim();

            var mCodigo = Regex.Match(text, @"\bCodigo\s+(\d+)\b");
            if (mCodigo.Success)
            {
                var num = int.Parse(mCodigo.Groups[1].Value);
                result.CodigoComprobante = mCodigo.Groups[1].Value;
                if (CodigoALetra.TryGetValue(num, out var letra)) result.Letra = letra;
            }

            var mNumero = Regex.Match(text, @"N[ºo]\s*(\d{4,5})-(\d{5,8})");
            if (mNumero.Success)
            {
                result.PuntoVenta = mNumero.Groups[1].Value;
                result.Numero = mNumero.Groups[2].Value;
            }
            else
            {
                result.Advertencias.Add("No se pudo extraer Punto de Venta / Número de comprobante.");
            }

            // La fecha de emisión está en la primera línea del documento ("<Ciudad>, DD.MM.AAAA"),
            // sin ninguna etiqueta que la identifique.
            result.FechaEmision = ParseFechaPuntos(lineas.FirstOrDefault());

            var mVence = Regex.Match(text, @"VENCE\s*:\s*(\d{1,2}\s+DE\s+\w+\s+DE\s+\d{4})", RegexOptions.IgnoreCase);
            result.FechaVencimientoPago = ParseFechaLarga(mVence.Success ? mVence.Groups[1].Value : null);

            // Se busca en las líneas originales (no en el texto aplanado): la línea de "COND.
            // VTA." comparte renglón visual con una línea de la dirección del receptor (2
            // columnas), así que al aplanar todo a una sola línea el texto de esa otra columna
            // queda pegado antes de "VENCE" y se cuela en la captura.
            var mCondVenta = lineas.Select(l => Regex.Match(l, @"COND\.\s*VTA\.\s*:\s*(.+)$"))
                .FirstOrDefault(m => m.Success);
            if (mCondVenta != null) result.CondicionVenta = mCondVenta.Groups[1].Value.Trim();

            // ---- Emisor ----
            // Acá "C.U.I.T.:" (con puntos) es del emisor y "CUIT:" (sin puntos) es del receptor
            // -al revés que en los formatos ARCA/SAE-, porque es otro software y no hay ninguna
            // razón para que use la misma convención de etiquetas.
            var mEmisorCuit = Regex.Match(text, @"C\.U\.I\.T\.?:\s*([\d-]{11,13})");
            if (mEmisorCuit.Success) result.ProveedorCuit = Regex.Replace(mEmisorCuit.Groups[1].Value, @"\D", "");
            else result.Advertencias.Add("No se pudo extraer el CUIT del emisor.");

            var mEmisorIIBB = Regex.Match(text, @"ING\.BRUTOS:\s*([\d-]+)");
            if (mEmisorIIBB.Success) result.ProveedorIngresosBrutos = mEmisorIIBB.Groups[1].Value;

            // El nombre del emisor no tiene etiqueta: es la segunda línea del documento (la
            // primera es la fecha). Es posicional -más frágil que el resto- así que se avisa.
            if (lineas.Count > 1)
                result.ProveedorRazonSocial = Regex.Replace(lineas[1], @"\s+[ABCM]$", "").Trim();
            else
                result.Advertencias.Add("No se pudo extraer la Razón Social del emisor (posicional, sin etiqueta).");

            // ---- Receptor ----
            var mReceptorCuit = Regex.Match(text, @"\bCUIT:\s*(\d{11})\s*IIBB:");
            if (mReceptorCuit.Success) result.ReceptorCuit = mReceptorCuit.Groups[1].Value;
            else result.Advertencias.Add("No se pudo extraer el CUIT del cliente (receptor).");

            // El nombre del receptor tampoco tiene etiqueta: es la línea siguiente a la primera
            // mención de "IVA Responsable Inscripto" (la del emisor), sin la referencia de
            // documento que puede venir pegada en la misma línea (columna derecha del layout).
            int idxIva = lineas.FindIndex(l => Regex.IsMatch(l, @"IVA\s+Responsable\s+Inscripto", RegexOptions.IgnoreCase));
            if (idxIva >= 0 && idxIva + 1 < lineas.Count)
            {
                var candidata = Regex.Replace(lineas[idxIva + 1], @"\s+\d{4,}\S*\s*$", "").Trim();
                result.ReceptorRazonSocial = candidata;
            }
            else
            {
                result.Advertencias.Add("No se pudo extraer el nombre del cliente (posicional, sin etiqueta).");
            }

            // La decisión de si es compra o venta se toma centralizada, después de aplicar el QR
            // (ver QrReconciliation.DecidirEsCompra), porque el QR puede corregir el CUIT emisor/receptor.

            // ---- Totales ----
            var mSubtotal = Regex.Match(text, @"SUB TOTAL\s*\$\s*(" + NUM + ")");
            if (mSubtotal.Success) result.Subtotal = result.ImporteNetoGravado = ParseNumeroOZero(mSubtotal.Groups[1].Value);
            else result.Advertencias.Add("No se pudo extraer el Subtotal del comprobante.");

            foreach (Match m in Regex.Matches(text, @"\bIVA\s+(\d+(?:[.,]\d+)?)\s*%\s+(" + NUM + ")"))
                AgregarIva(result, m.Groups[1].Value, ParseNumeroOZero(m.Groups[2].Value));

            // "TOTAL" es también la cola de "SUB TOTAL" (el \b no lo evita: hay un espacio antes
            // de "TOTAL" en ambos casos), así que se excluye explícitamente ese caso.
            var mTotal = Regex.Match(text, @"(?<!SUB )\bTOTAL\s*\$\s*(" + NUM + ")");
            if (mTotal.Success) result.ImporteTotal = ParseNumeroOZero(mTotal.Groups[1].Value);
            else result.Advertencias.Add("No se pudo extraer el Total del comprobante.");

            var mCae = Regex.Match(text, @"\bCAE\s+(\d+)\b");
            if (mCae.Success) result.Cae = mCae.Groups[1].Value;
            else result.Advertencias.Add("No se pudo extraer el CAE del comprobante.");

            var mVtoCae = Regex.Match(text, @"FECHA VTO:\s*(\d{2}\.\d{2}\.\d{4})");
            result.FechaVtoCae = ParseFechaPuntos(mVtoCae.Success ? mVtoCae.Groups[1].Value : null);

            // ---- Ítems ----
            // Cada ítem vive, en general, en una sola línea de texto (CODIGO CONCEPTO CANTIDAD
            // U.M. LOTE DESPACHO PCIO.UNIT MONEDA IMPORTE); si la descripción se envuelve a una
            // segunda línea (sin ninguno de esos números), se agrega a la descripción del ítem
            // anterior en vez de tratarse como un ítem aparte.
            var itemRegex = new Regex(@"^(?<codigo>\d{3,6})\s+(?<desc>.+?)\s+(?<cantidad>" + NUM + @")\s+(?<um>\S+)\s+(?<lote>\S+)\s+(?<despacho>\S+)\s+(?<precio>" + NUM + @")\s+(?<moneda>\S+)\s+(?<importe>" + NUM + @")\s*$");
            DetalleFactura? ultimoItem = null;
            bool enTabla = false;
            foreach (var linea in lineas)
            {
                if (Regex.IsMatch(linea, @"^CODIGO\s+CONCEPTO")) { enTabla = true; continue; }
                if (!enTabla) continue;
                if (Regex.IsMatch(linea, @"^SUB TOTAL\b")) break;
                if (Regex.IsMatch(linea, @"^_+$")) continue;

                var m = itemRegex.Match(linea);
                if (m.Success)
                {
                    ultimoItem = new DetalleFactura
                    {
                        Codigo = m.Groups["codigo"].Value,
                        Concepto = m.Groups["desc"].Value.Trim(),
                        Cantidad = ParseNumeroOZero(m.Groups["cantidad"].Value),
                        UnidadMedida = m.Groups["um"].Value,
                        PrecioUnitario = ParseNumeroOZero(m.Groups["precio"].Value),
                        Subtotal = ParseNumeroOZero(m.Groups["importe"].Value),
                    };
                    result.Detalle.Add(ultimoItem);
                }
                else if (ultimoItem != null)
                {
                    // Continuación de la descripción del ítem anterior (envuelta a otra línea).
                    ultimoItem.Concepto = (ultimoItem.Concepto + " " + linea).Trim();
                }
            }

            if (result.Detalle.Count == 0)
                result.Advertencias.Add("No se pudo detectar ningún ítem de detalle en la tabla del comprobante.");

            return result;
        }
    }
}
