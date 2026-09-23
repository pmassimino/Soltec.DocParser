using System.Globalization;
using System.Text.RegularExpressions;
using Soltec.DocParser.Models;

namespace Soltec.DocParser.Services
{
    // Parser para un tercer formato de factura (visto por primera vez en comprobantes de
    // AGRIPUERTO S.A., de acopio/servicios de granos), distinto tanto de ARCA/AFIP como del
    // formato SAE: usa sus propias etiquetas ("Sres:" en vez de "Cliente"/"Apellido y Nombre",
    // "I.V.A.:"/"Cond. Vta.:", tabla de ítems "Artículo Descripción Cantidad U.M. Alicuota IVA
    // Precio Importe", totales "Gravado/Exento/IVA Inscripto al X%/Perc. I.B./Perc. I.V.A./Total").
    // El nombre del parser se basa en esa estructura (Gravado/Exento), no en la empresa, por si
    // otro proveedor usa el mismo software (ya pasó antes con el formato "SAE").
    //
    // A diferencia de ARCA y SAE, acá cada ítem vive en una sola línea de texto (sin columnas
    // descuadradas), así que se puede extraer directo por regex sobre esa línea en vez de
    // reconstruir columnas por coordenadas.
    public static class FacturaGravadoParser
    {
        static readonly Dictionary<int, string> CodigoALetra = new()
        {
            [1] = "A", [6] = "B", [11] = "C", [51] = "M",
        };

        const string NUM = @"[\d.,]+";

        static decimal ParseNumeroOZero(string? s)
        {
            if (string.IsNullOrWhiteSpace(s)) return 0m;
            s = s.Trim();
            // Formato argentino: "." miles, "," decimal (534.600,00 / 178.200,0000).
            return decimal.TryParse(s.Replace(".", "").Replace(",", "."), NumberStyles.Any, CultureInfo.InvariantCulture, out var v) ? v : 0m;
        }

        static DateTime? ParseArDate(string? s)
        {
            if (string.IsNullOrWhiteSpace(s)) return null;
            return DateTime.TryParseExact(s.Trim(), "dd/MM/yyyy", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? d : null;
        }

        public static bool EsFormatoGravado(string lineText)
        {
            return Regex.IsMatch(lineText, @"\bSres:") && Regex.IsMatch(lineText, @"\bGravado\b")
                && Regex.IsMatch(lineText, @"\bExento\b") && Regex.IsMatch(lineText, @"CAE No\.:");
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

        static void AgregarOtroTributo(Factura result, string id, decimal importe)
        {
            if (importe == 0) return;
            var existente = result.OtrosTributos.FirstOrDefault(o => o.Id == id);
            if (existente != null) existente.Importe += importe;
            else result.OtrosTributos.Add(new ImporteConId { Id = id, Importe = importe });
        }

        public static Factura Parse(string lineText, List<PositionedWord> words, string empresaCuit)
        {
            var result = new Factura { TipoComprobante = "FACTURA" };
            string text = Regex.Replace(lineText, @"\s+", " ").Trim();

            var mCodigo = Regex.Match(text, @"Cod\.(\d+)");
            if (mCodigo.Success)
            {
                result.CodigoComprobante = mCodigo.Groups[1].Value;
                if (int.TryParse(mCodigo.Groups[1].Value, out var codigoInt) && CodigoALetra.TryGetValue(codigoInt, out var letra))
                    result.Letra = letra;
            }

            var mNumero = Regex.Match(text, @"N[ºo]\s*(\d{4})-?\s*(\d{5,8})");
            if (mNumero.Success)
            {
                result.PuntoVenta = mNumero.Groups[1].Value;
                result.Numero = mNumero.Groups[2].Value;
            }
            else
            {
                result.Advertencias.Add("No se pudo extraer Punto de Venta / Número de comprobante.");
            }

            var mFecha = Regex.Match(text, @"Fecha de Registro\s*:\s*(\d{2}/\d{2}/\d{4})");
            result.FechaEmision = ParseArDate(mFecha.Success ? mFecha.Groups[1].Value : null);

            var mFechaVto = Regex.Match(text, @"Vencimientos\s+(\d{2}/\d{2}/\d{4})");
            result.FechaVencimientoPago = ParseArDate(mFechaVto.Success ? mFechaVto.Groups[1].Value : null);

            var mCondVenta = Regex.Match(text, @"Cond\.\s*Vta\.:\s*(.+?)\s*(?=Art[ií]culo|$)");
            if (mCondVenta.Success) result.CondicionVenta = mCondVenta.Groups[1].Value.Trim();

            // ---- Emisor / Receptor ----
            // El primer "CUIT:" es del emisor (aparece antes de "Sres:"); el/los siguientes son
            // del receptor -se toma el más cercano a "Sres:" en vez de asumir que es siempre el
            // segundo, por si el layout cambia de orden como ya pasó con el formato SAE.
            int idxSres = text.IndexOf("Sres:", StringComparison.Ordinal);
            var todosLosCuit = Regex.Matches(text, @"CUIT:\s*([\d-]{11,13})").Cast<Match>().ToList();
            if (idxSres >= 0 && todosLosCuit.Count > 0)
            {
                var cuitReceptor = todosLosCuit.Where(m => m.Index > idxSres).OrderBy(m => m.Index).FirstOrDefault()
                    ?? todosLosCuit.OrderBy(m => Math.Abs(m.Index - idxSres)).First();
                result.ReceptorCuit = Regex.Replace(cuitReceptor.Groups[1].Value, @"\D", "");
                var cuitEmisor = todosLosCuit.FirstOrDefault(m => m != cuitReceptor);
                if (cuitEmisor != null) result.ProveedorCuit = Regex.Replace(cuitEmisor.Groups[1].Value, @"\D", "");
            }
            else if (todosLosCuit.Count > 0)
            {
                result.ProveedorCuit = Regex.Replace(todosLosCuit[0].Groups[1].Value, @"\D", "");
            }

            var mEmisorNombre = Regex.Match(text, @"^FACTURA\s+(.+?)\s*CUIT:");
            if (mEmisorNombre.Success) result.ProveedorRazonSocial = mEmisorNombre.Groups[1].Value.Trim();

            var mEmisorIIBB = Regex.Match(text, @"Ing\.Brutos:\s*(\d+)");
            if (mEmisorIIBB.Success) result.ProveedorIngresosBrutos = mEmisorIIBB.Groups[1].Value;

            var mReceptorNombre = Regex.Match(text, @"Sres:\s*(?:\d+\s+)?(.+?)\s*(?=ING\.\s*BRUTOS|Domicilio:|CUIT:|$)");
            if (mReceptorNombre.Success) result.ReceptorRazonSocial = mReceptorNombre.Groups[1].Value.Trim();
            else result.Advertencias.Add("No se pudo extraer el nombre del cliente (receptor).");

            if (string.IsNullOrEmpty(result.ReceptorCuit))
                result.Advertencias.Add("No se pudo extraer el CUIT del cliente (receptor).");
            if (string.IsNullOrEmpty(result.ProveedorCuit))
                result.Advertencias.Add("No se pudo extraer el CUIT del emisor.");

            // La decisión de si es compra o venta se toma centralizada, después de aplicar el QR
            // (ver QrReconciliation.DecidirEsCompra), porque el QR puede corregir el CUIT emisor/receptor.

            // ---- Totales ----
            var mGravado = Regex.Match(text, @"Gravado\s*\$\s*(" + NUM + ")");
            if (mGravado.Success) result.Subtotal = result.ImporteNetoGravado = ParseNumeroOZero(mGravado.Groups[1].Value);

            var mExento = Regex.Match(text, @"Exento\s*\$\s*(" + NUM + ")");
            if (mExento.Success) AgregarOtroTributo(result, "Exento", ParseNumeroOZero(mExento.Groups[1].Value));

            foreach (Match m in Regex.Matches(text, @"IVA Inscripto al\s*:?\s*(\d+(?:[.,]\d+)?)\s*%\s*=?\s*(" + NUM + ")"))
                AgregarIva(result, m.Groups[1].Value, ParseNumeroOZero(m.Groups[2].Value));

            var mPercIB = Regex.Match(text, @"Perc\.\s*I\.B\.\s*\$\s*(" + NUM + ")");
            if (mPercIB.Success) AgregarOtroTributo(result, "PercIB", ParseNumeroOZero(mPercIB.Groups[1].Value));

            var mPercIva = Regex.Match(text, @"Perc\.\s*I\.V\.A\.\s*\$\s*(" + NUM + ")");
            if (mPercIva.Success) AgregarOtroTributo(result, "PercIva", ParseNumeroOZero(mPercIva.Groups[1].Value));

            var mTotal = Regex.Match(text, @"\bTotal\s*\$\s*(" + NUM + ")");
            if (mTotal.Success) result.ImporteTotal = ParseNumeroOZero(mTotal.Groups[1].Value);
            else result.Advertencias.Add("No se pudo extraer el Total del comprobante.");

            var mCae = Regex.Match(text, @"CAE No\.:\s*(\d+)");
            if (mCae.Success) result.Cae = mCae.Groups[1].Value;
            else result.Advertencias.Add("No se pudo extraer el CAE del comprobante.");

            var mVtoCae = Regex.Match(text, @"Fecha Vto\.:\s*(\d{2}/\d{2}/\d{4})");
            result.FechaVtoCae = ParseArDate(mVtoCae.Success ? mVtoCae.Groups[1].Value : null);

            // ---- Ítems ----
            // Cada ítem vive en una sola línea de texto (sin columnas descuadradas como en ARCA/
            // SAE), reconocible porque trae "Kilos:" y "Tarifa:" en la descripción.
            foreach (var linea in lineText.Split('\n'))
            {
                if (!Regex.IsMatch(linea, @"Kilos:", RegexOptions.IgnoreCase) || !Regex.IsMatch(linea, @"Tarifa:", RegexOptions.IgnoreCase))
                    continue;

                var mItem = Regex.Match(linea.Trim(), @"^(?<desc>.+?)\s*,00\s+(?<alicuota>\d+(?:[.,]\d+)?)\s+(?<precio>" + NUM + @")\s*$");
                if (!mItem.Success)
                {
                    result.Advertencias.Add($"No se pudo interpretar la línea de ítem: '{linea.Trim()}'.");
                    continue;
                }

                var detalle = new DetalleFactura
                {
                    Concepto = mItem.Groups["desc"].Value.Trim(),
                    AlicuotaIva = ParseNumeroOZero(mItem.Groups["alicuota"].Value.Replace(",", ".")),
                    PrecioUnitario = ParseNumeroOZero(mItem.Groups["precio"].Value),
                };
                detalle.Subtotal = detalle.PrecioUnitario;
                result.Detalle.Add(detalle);
            }

            if (result.Detalle.Count == 0)
                result.Advertencias.Add("No se pudo detectar ningún ítem de detalle en la tabla del comprobante.");

            return result;
        }
    }
}
