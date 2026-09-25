using Soltec.DocParser.Models;

namespace Soltec.DocParser.Services
{
    // Aplica los datos del QR estándar de AFIP/ARCA como fuente principal para los campos de
    // encabezado que trae (Numero/PuntoVenta, CUIT emisor, Fecha, Importe Total, CAE, Letra vía
    // TipoCmp), porque no depende del layout de cada proveedor y ya viene mostrando ser más
    // confiable que el texto en varios casos reales. El resto (receptor por nombre, detalle de
    // ítems, IVA discriminado) sigue viniendo del parser de texto, porque el QR no lo trae.
    //
    // Un campo del QR ausente o inválido no pisa el valor de texto: se deja el de texto y se
    // avisa que el QR no lo pudo confirmar (se vio un comprobante real con el campo "nroCmp" del
    // QR corrupto por un bug del software emisor, no algo que se pueda asumir que nunca pasa).
    public static class QrReconciliation
    {
        static readonly Dictionary<int, string> CodigoALetra = new()
        {
            [1] = "A", [2] = "A", [3] = "A",
            [6] = "B", [7] = "B", [8] = "B",
            [11] = "C", [12] = "C", [13] = "C",
            [51] = "M", [52] = "M", [53] = "M",
            [201] = "A", [202] = "A", [203] = "A",
            [206] = "B", [207] = "B", [208] = "B",
            [211] = "C", [212] = "C", [213] = "C",
        };

        public static void Aplicar(Factura factura, QrData? qr)
        {
            if (qr == null)
            {
                factura.Advertencias.Add("No se pudo leer el código QR del comprobante; los datos de encabezado quedan solo como los extrajo el parser de texto.");
                return;
            }
            if (qr.CamposInvalidos.Count > 0)
                factura.Advertencias.Add($"El QR del comprobante trae mal generados estos campos (bug del software emisor, no de la lectura): {string.Join(", ", qr.CamposInvalidos)}. Se usa el valor de texto para esos campos.");

            if (qr.TipoCmp.HasValue)
            {
                // Comparación numérica, no de texto: "011" (con cero a la izquierda, como lo
                // captura algún parser) y "11" (como lo da el QR) son el mismo código.
                bool coincideNumericamente = int.TryParse(factura.CodigoComprobante, out var codigoTexto) && codigoTexto == qr.TipoCmp.Value;
                if (!coincideNumericamente && !string.IsNullOrEmpty(factura.CodigoComprobante))
                    factura.Advertencias.Add($"Código de comprobante: el texto decía '{factura.CodigoComprobante}' pero el QR dice '{qr.TipoCmp.Value}'; se usa el del QR.");
                if (!coincideNumericamente) factura.CodigoComprobante = qr.TipoCmp.Value.ToString();
                if (CodigoALetra.TryGetValue(qr.TipoCmp.Value, out var letra)) factura.Letra = letra;
            }

            factura.PuntoVenta = ReconciliarNumero(factura.PuntoVenta, qr.PtoVta, 4, factura.Advertencias, "Punto de Venta");
            factura.Numero = ReconciliarNumero(factura.Numero, qr.NroCmp, 8, factura.Advertencias, "Número de comprobante");

            if (qr.Fecha.HasValue)
            {
                if (factura.FechaEmision.HasValue && factura.FechaEmision.Value.Date != qr.Fecha.Value.Date)
                    factura.Advertencias.Add($"Fecha de emisión: el texto decía '{factura.FechaEmision:dd/MM/yyyy}' pero el QR dice '{qr.Fecha:dd/MM/yyyy}'; se usa la del QR.");
                factura.FechaEmision = qr.Fecha;
            }

            if (qr.Importe.HasValue)
            {
                if (factura.ImporteTotal != 0 && Math.Abs(factura.ImporteTotal - qr.Importe.Value) >= 0.02m)
                    factura.Advertencias.Add($"Importe Total: el texto decía '{factura.ImporteTotal:F2}' pero el QR dice '{qr.Importe:F2}'; se usa el del QR.");
                factura.ImporteTotal = qr.Importe.Value;
            }

            if (!string.IsNullOrEmpty(qr.CodAut))
            {
                if (!string.IsNullOrEmpty(factura.Cae) && factura.Cae != qr.CodAut)
                    factura.Advertencias.Add($"CAE: el texto decía '{factura.Cae}' pero el QR dice '{qr.CodAut}'; se usa el del QR.");
                factura.Cae = qr.CodAut;
            }

            if (!string.IsNullOrEmpty(qr.Cuit))
            {
                if (!string.IsNullOrEmpty(factura.ProveedorCuit) && factura.ProveedorCuit != qr.Cuit)
                    factura.Advertencias.Add($"CUIT del emisor: el texto decía '{factura.ProveedorCuit}' pero el QR dice '{qr.Cuit}'; se usa el del QR.");
                factura.ProveedorCuit = qr.Cuit;
            }

            // El receptor solo se toma del QR si su documento es un CUIT (tipoDocRec=80); otros
            // tipos de documento (DNI, etc.) no corresponden al campo ReceptorCuit.
            if (qr.TipoDocRec == "80" && !string.IsNullOrEmpty(qr.NroDocRec))
            {
                if (!string.IsNullOrEmpty(factura.ReceptorCuit) && factura.ReceptorCuit != qr.NroDocRec)
                    factura.Advertencias.Add($"CUIT del receptor: el texto decía '{factura.ReceptorCuit}' pero el QR dice '{qr.NroDocRec}'; se usa el del QR.");
                factura.ReceptorCuit = qr.NroDocRec;
            }
        }

        static string? ReconciliarNumero(string? textoValue, long? qrValue, int padDefault, List<string> advertencias, string nombreCampo)
        {
            if (!qrValue.HasValue) return textoValue;

            bool textoParseo = long.TryParse(textoValue, out var textoNum);
            if (textoParseo && textoNum == qrValue.Value)
                return textoValue; // coinciden: se conserva el formato/padding tal como lo imprime el comprobante

            if (textoParseo && textoNum != qrValue.Value)
                advertencias.Add($"{nombreCampo}: el texto decía '{textoValue}' pero el QR dice '{qrValue}'; se usa el del QR.");

            return qrValue.Value.ToString().PadLeft(padDefault, '0');
        }
    }
}
