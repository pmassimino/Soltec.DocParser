using Soltec.DocParser.Models;

namespace Soltec.DocParser.Services
{
    // Elige qué IA usar cuando el parser por reglas no pudo leer el comprobante. Los proveedores
    // se prueban en el orden de Ia:Orden (appsettings), saltando los que no tienen API key, y el
    // primero que devuelve una factura gana; si uno falla (caído, sin saldo, respuesta inválida)
    // se pasa al siguiente. El caller puede forzar uno con ?proveedorIa=deepseek|claude.
    public static class IaFallback
    {
        public const string DeepSeek = "DeepSeek";
        public const string Claude = "Claude";

        static List<string> _orden = new() { DeepSeek, Claude };

        public static void Configurar(IEnumerable<string>? orden)
        {
            var lista = orden?.Where(p => !string.IsNullOrWhiteSpace(p)).Select(Normalizar).Where(p => p != null).Select(p => p!).Distinct().ToList();
            if (lista is { Count: > 0 }) _orden = lista;
        }

        public static bool AlgunoDisponible => DeepSeekExtraction.Disponible || AiExtraction.Disponible;

        // "deepseek" -> "DeepSeek", "claude"/"anthropic" -> "Claude", cualquier otra cosa -> null.
        public static string? Normalizar(string? proveedor) => proveedor?.Trim().ToLowerInvariant() switch
        {
            "deepseek" => DeepSeek,
            "claude" or "anthropic" => Claude,
            _ => null,
        };

        // bytes/mediaType: el documento original (PDF o imagen, como vino). textoExtraido: el texto
        // de PdfPig u OCR, puede estar vacío. Devuelve la factura y el proveedor que la resolvió,
        // o (null, null) con los motivos en errores. En llamadas quedan todos los intentos hechos a
        // las APIs (de todos los proveedores probados), con sus tokens, para el registro de uso.
        public static async Task<(Factura? factura, string? proveedor)> ExtraerAsync(
            byte[] bytes, string mediaType, string extension, string textoExtraido, string? proveedorForzado, List<string> errores, List<LlamadaIa> llamadas)
        {
            List<string> proveedores;
            if (proveedorForzado != null)
            {
                var forzado = Normalizar(proveedorForzado);
                if (forzado == null)
                {
                    errores.Add($"Proveedor de IA desconocido: '{proveedorForzado}' (usar deepseek o claude).");
                    return (null, null);
                }
                proveedores = new() { forzado };
            }
            else
            {
                proveedores = _orden;
            }

            bool alguno = false;
            foreach (var proveedor in proveedores)
            {
                Factura? factura = null;
                switch (proveedor)
                {
                    case DeepSeek:
                        if (!DeepSeekExtraction.Disponible)
                        {
                            if (proveedorForzado != null) errores.Add("DeepSeek no está configurado (falta DeepSeek:ApiKey).");
                            continue;
                        }
                        alguno = true;
                        factura = await DeepSeekExtraction.ExtraerAsync(bytes, mediaType, textoExtraido, errores, llamadas);
                        break;

                    case Claude:
                        if (!AiExtraction.Disponible)
                        {
                            if (proveedorForzado != null) errores.Add("Claude no está configurado (falta Anthropic:ApiKey).");
                            continue;
                        }
                        alguno = true;
                        // Claude acepta el PDF tal cual; de las imágenes solo jpeg/png/gif/webp.
                        var (bytesClaude, mediaClaude) = mediaType == "application/pdf"
                            ? (bytes, mediaType)
                            : AiExtraction.PrepararImagen(bytes, extension);
                        factura = await AiExtraction.ExtraerAsync(bytesClaude, mediaClaude, errores, llamadas);
                        break;
                }

                if (factura != null)
                    return (factura, proveedor);
            }

            if (!alguno && proveedorForzado == null)
                errores.Add("El fallback por IA no está configurado: falta DeepSeek:ApiKey o Anthropic:ApiKey.");
            return (null, null);
        }
    }
}
