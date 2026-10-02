using System.Globalization;
using System.Text.RegularExpressions;
using Soltec.DocParser.Models;

namespace Soltec.DocParser.Services
{
    // Detecta desde el texto del comprobante si está hecho en dólares y a qué cotización. Solo se
    // considera dólares si el comprobante lo dice de forma explícita (etiqueta de moneda, "expresado
    // en dólares", cotización/tipo de cambio); una mención suelta de "USD" en la descripción de un
    // ítem no alcanza. Sin esa evidencia queda en pesos (el default de Factura). Si además hay QR,
    // QrReconciliation lo pisa: el QR es más confiable que el texto.
    public static class MonedaDetector
    {
        const string Dolar = @"(?:d[oó]lar(?:es)?(?:\s+(?:estadounidenses?|americanos?))?|us\s?d|u\$s|u\$d|us\$)";
        const string NUM = @"(?:\d{1,3}(?:\.\d{3})+|\d+)(?:,\d+)?";

        static readonly Regex RxMoneda = new(
            @"(?:Moneda|Divisa)\s*(?:de\s+(?:la\s+)?(?:operaci[oó]n|factura))?\s*[:\-]?\s*" + Dolar + @"\b"
            + @"|(?:expresad[oa]s?|emitid[oa]|facturad[oa]|importes?)\s+en\s+" + Dolar + @"\b",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        static readonly Regex RxCotizacion = new(
            @"(?:Cotizaci[oó]n(?:\s+(?:del\s+)?(?:d[oó]lar|divisa|moneda))?|Tipo\s+de\s+cambio|T\.?\s?C\.?)\s*(?:\(?\s*(?:" + Dolar + @")?\s*\)?)\s*[:=]?\s*\$?\s*(" + NUM + ")",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        public static void Aplicar(Factura factura, string texto)
        {
            if (string.IsNullOrWhiteSpace(texto)) return;
            string t = Regex.Replace(texto, @"\s+", " ");

            var mCotizacion = RxCotizacion.Match(t);
            decimal cotizacion = 0m;
            if (mCotizacion.Success)
                decimal.TryParse(mCotizacion.Groups[1].Value.Replace(".", "").Replace(",", "."), NumberStyles.Number, CultureInfo.InvariantCulture, out cotizacion);

            if (!RxMoneda.IsMatch(t)) return;

            factura.Moneda = "USD";
            factura.Cotizacion = cotizacion;
            if (cotizacion <= 0)
                factura.Advertencias.Add("El comprobante está en dólares pero no se pudo extraer la cotización; cargarla manualmente.");
        }
    }
}
