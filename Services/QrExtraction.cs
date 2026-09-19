using System.Text.Json;
using System.Text.RegularExpressions;
using PDFtoImage;
using SkiaSharp;
using ZXing;
using ZXing.SkiaSharp;

namespace Soltec.DocParser.Services
{
    // Datos del QR estándar de AFIP/ARCA (RG 4892) que traen todos los comprobantes electrónicos,
    // sin importar qué software los generó -a diferencia del resto del documento, cuyo layout
    // varía por proveedor. Cada campo es nullable porque, en la práctica, el software que generó
    // ALGUNA factura puede tener bugs propios y dejar un campo puntual corrupto (se vio un caso
    // real donde "nroCmp" no era un número válido) sin que el resto del QR deje de servir.
    public class QrData
    {
        public string? Cuit { get; set; }
        public int? PtoVta { get; set; }
        public int? TipoCmp { get; set; }
        public long? NroCmp { get; set; }
        public decimal? Importe { get; set; }
        public string? Moneda { get; set; }
        public DateTime? Fecha { get; set; }
        public string? TipoDocRec { get; set; }
        public string? NroDocRec { get; set; }
        public string? CodAut { get; set; }

        // Campos del JSON del QR que no se pudieron interpretar (p.ej. "nroCmp" con un valor no
        // numérico); se listan para que quede visible qué no se pudo usar, en vez de fallar callado.
        public List<string> CamposInvalidos { get; set; } = new();
    }

    public static class QrExtraction
    {
        // El QR es chico en algunos comprobantes (se vio un caso real donde 300 DPI no alcanzaba
        // para que el lector lo reconozca y 600 sí), así que se reintenta con más resolución en
        // vez de fijar un DPI único: la mayoría se resuelve rápido en el primer intento, y solo
        // los casos difíciles pagan el costo extra de un render más grande.
        static readonly int[] DpisAReintentar = { 300, 600, 900 };

        public static QrData? TryExtraerQr(byte[] pdfBytes, int paginaIndex = 0)
        {
            string? texto = null;
            foreach (var dpi in DpisAReintentar)
            {
                try
                {
                    using SKBitmap bitmap = Conversion.ToImage(pdfBytes, page: paginaIndex, options: new RenderOptions(Dpi: dpi));
                    var reader = new BarcodeReader<SKBitmap>(bmp => new SKBitmapLuminanceSource(bmp))
                    {
                        Options = new ZXing.Common.DecodingOptions
                        {
                            PossibleFormats = new List<BarcodeFormat> { BarcodeFormat.QR_CODE },
                            TryHarder = true,
                        }
                    };
                    texto = reader.Decode(bitmap)?.Text;
                }
                catch
                {
                    return null; // PDF de una sola página rara, o sin bitmap renderizable; no es fatal.
                }
                if (!string.IsNullOrEmpty(texto)) break;
            }

            if (string.IsNullOrEmpty(texto)) return null;

            var mParam = Regex.Match(texto, @"[?&]p=([A-Za-z0-9+/=_-]+)");
            if (!mParam.Success) return null;

            string json;
            try
            {
                string base64 = mParam.Groups[1].Value.Replace('-', '+').Replace('_', '/');
                json = System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(base64));
            }
            catch
            {
                return null;
            }

            return ParsearJson(json);
        }

        // El JSON del QR puede venir con algún campo mal generado por el software emisor (visto
        // en un comprobante real: "nroCmp":A0009163, no es JSON válido). En vez de descartar todo
        // el QR por un campo roto, se parsea campo por campo con su propio try/catch.
        static QrData ParsearJson(string json)
        {
            var result = new QrData();
            JsonDocument? doc = null;
            try
            {
                doc = JsonDocument.Parse(json);
            }
            catch
            {
                // JSON inválido en su conjunto: se intenta rescatar campo por campo igual, por si
                // el problema es uno solo (p.ej. un valor sin comillas) y el resto se puede leer
                // con una extracción más laxa por regex clave:valor.
                RescatarCamposPorRegex(json, result);
                return result;
            }

            using (doc)
            {
                var root = doc.RootElement;
                TryLeer(root, "cuit", result.CamposInvalidos, v => result.Cuit = NumeroLargoATexto(v));
                TryLeer(root, "ptoVta", result.CamposInvalidos, v => result.PtoVta = (int)v.GetDouble());
                TryLeer(root, "tipoCmp", result.CamposInvalidos, v => result.TipoCmp = (int)v.GetDouble());
                TryLeer(root, "nroCmp", result.CamposInvalidos, v => result.NroCmp = (long)v.GetDouble());
                TryLeer(root, "importe", result.CamposInvalidos, v => result.Importe = v.GetDecimal());
                TryLeer(root, "moneda", result.CamposInvalidos, v => result.Moneda = v.GetString());
                TryLeer(root, "fecha", result.CamposInvalidos, v => result.Fecha = DateTime.Parse(v.GetString()!));
                TryLeer(root, "tipoDocRec", result.CamposInvalidos, v => result.TipoDocRec = ((int)v.GetDouble()).ToString());
                TryLeer(root, "nroDocRec", result.CamposInvalidos, v => result.NroDocRec = NumeroLargoATexto(v));
                TryLeer(root, "codAut", result.CamposInvalidos, v => result.CodAut = NumeroLargoATexto(v));
            }

            return result;
        }

        static void TryLeer(JsonElement root, string campo, List<string> invalidos, Action<JsonElement> asignar)
        {
            if (!root.TryGetProperty(campo, out var v)) return;
            try { asignar(v); }
            catch { invalidos.Add(campo); }
        }

        static string NumeroLargoATexto(JsonElement v) =>
            v.ValueKind == JsonValueKind.String ? v.GetString()! : v.GetDouble().ToString("F0", System.Globalization.CultureInfo.InvariantCulture);

        static void RescatarCamposPorRegex(string json, QrData result)
        {
            string? Buscar(string campo) => Regex.Match(json, "\"" + campo + "\"\\s*:\\s*\"?([^,\"}]+)\"?").Groups[1] is { Success: true } g ? g.Value : null;

            var cuit = Buscar("cuit"); if (cuit != null) result.Cuit = cuit;
            var fecha = Buscar("fecha"); if (fecha != null && DateTime.TryParse(fecha, out var f)) result.Fecha = f; else if (fecha != null) result.CamposInvalidos.Add("fecha");
            var ptoVta = Buscar("ptoVta"); if (ptoVta != null && int.TryParse(ptoVta, out var pv)) result.PtoVta = pv; else if (ptoVta != null) result.CamposInvalidos.Add("ptoVta");
            var tipoCmp = Buscar("tipoCmp"); if (tipoCmp != null && int.TryParse(tipoCmp, out var tc)) result.TipoCmp = tc; else if (tipoCmp != null) result.CamposInvalidos.Add("tipoCmp");
            var nroCmp = Buscar("nroCmp"); if (nroCmp != null && long.TryParse(nroCmp, out var nc)) result.NroCmp = nc; else if (nroCmp != null) result.CamposInvalidos.Add("nroCmp");
            var importe = Buscar("importe"); if (importe != null && decimal.TryParse(importe, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var imp)) result.Importe = imp; else if (importe != null) result.CamposInvalidos.Add("importe");
            var moneda = Buscar("moneda"); if (moneda != null) result.Moneda = moneda;
            var tipoDocRec = Buscar("tipoDocRec"); if (tipoDocRec != null) result.TipoDocRec = tipoDocRec;
            var nroDocRec = Buscar("nroDocRec"); if (nroDocRec != null) result.NroDocRec = nroDocRec;
            var codAut = Buscar("codAut"); if (codAut != null) result.CodAut = codAut;
        }
    }
}
