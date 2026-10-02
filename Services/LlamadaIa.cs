using System.Text.Json;

namespace Soltec.DocParser.Services
{
    // Una llamada a la API de un proveedor de IA hecha mientras se procesaba un comprobante, con
    // los tokens que informó la respuesta. Se registra cada intento -no solo el que resolvió la
    // factura- porque los que fallaron después de responder (JSON inválido, respuesta cortada)
    // también se cobran.
    public sealed class LlamadaIa
    {
        public string Proveedor { get; set; } = "";
        public string Modelo { get; set; } = "";
        // Total de tokens de entrada, incluidos los que se leyeron de caché.
        public int TokensEntrada { get; set; }
        // La parte de TokensEntrada que fue un acierto de caché (se cobra más barato).
        public int TokensEntradaCache { get; set; }
        public int TokensSalida { get; set; }
        // true = esta llamada devolvió una factura utilizable.
        public bool Exitosa { get; set; }

        internal static int LeerEntero(JsonElement e, string prop) =>
            e.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var n) ? n : 0;

        internal void LeerModelo(JsonElement respuesta)
        {
            if (respuesta.TryGetProperty("model", out var m) && m.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(m.GetString()))
                Modelo = m.GetString()!;
        }
    }
}
