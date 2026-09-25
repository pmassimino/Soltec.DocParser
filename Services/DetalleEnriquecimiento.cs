using System.Text.RegularExpressions;
using Soltec.DocParser.Models;

namespace Soltec.DocParser.Services
{
    // Extrae, de la descripción de un ítem, datos que viven embebidos en el texto libre en vez
    // de en su propia columna -típico en facturas de acopio/flete de granos: CTG (siempre 11
    // dígitos, aunque la etiqueta varíe: "CP", "CTG", "N° de CTG", "carta de porte"), peso
    // ("Kilos: X") y tarifa ("Tarifa: X"). No depende de qué parser generó el ítem, así que
    // cualquier formato que traiga esta información en la descripción se beneficia igual.
    public static class DetalleEnriquecimiento
    {
        public static void EnriquecerConCtgPesoTarifa(DetalleFactura detalle)
        {
            string texto = detalle.Concepto;
            if (string.IsNullOrWhiteSpace(texto)) return;

            // El CTG siempre tiene 11 dígitos, pero puede venir con ceros a la izquierda pegados
            // al código de planta (p.ej. "CP-0010-010234396441", donde el CTG real es
            // "10234396441"): se captura de a 11 dígitos después de descartar los ceros que sobren.
            // Entre la etiqueta y el número puede haber un "N°"/"Nº"/"No" (p.ej. "CTG Nº 123...").
            // La etiqueta CTG puede venir con puntos ("C.T.G.N°10234950325").
            const string Nro = @"(?:N[°ºo]\.?\s*)?[\s\-:]*";
            var mCtg = Regex.Match(texto, @"CP[\s\-:]*" + Nro + @"\d{2,4}[\s\-]*0*(\d{11})\b", RegexOptions.IgnoreCase);
            if (!mCtg.Success)
                mCtg = Regex.Match(texto, @"C\.?\s*T\.?\s*G\.?[\s\-:]*" + Nro + "0*(\\d{11})\\b", RegexOptions.IgnoreCase);
            if (!mCtg.Success)
                mCtg = Regex.Match(texto, @"[Cc]arta\s+de\s+[Pp]orte[\s\-:]*" + Nro + "0*(\\d{11})\\b");
            if (mCtg.Success)
                detalle.Ctg = mCtg.Groups[1].Value;

            var mPeso = Regex.Match(texto, @"Kilos?:?\s*([\d.,]+)", RegexOptions.IgnoreCase);
            if (mPeso.Success && decimal.TryParse(mPeso.Groups[1].Value.Replace(".", "").Replace(",", "."), System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var peso))
                detalle.Peso = peso;

            var mTarifa = Regex.Match(texto, @"Tarifa:?\s*([\d.,]+)", RegexOptions.IgnoreCase);
            if (mTarifa.Success && decimal.TryParse(mTarifa.Groups[1].Value.Replace(".", "").Replace(",", "."), System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var tarifa))
                detalle.Tarifa = tarifa;
        }
    }
}
