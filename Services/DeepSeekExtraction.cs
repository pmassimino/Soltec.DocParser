using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using PDFtoImage;
using SkiaSharp;
using Soltec.DocParser.Models;

namespace Soltec.DocParser.Services
{
    // Fallback por IA con DeepSeek (API compatible con OpenAI: /chat/completions). Hace lo mismo
    // que AiExtraction con Claude -extraer la factura cuando el parser por reglas no pudo-, con
    // dos diferencias impuestas por la API:
    //
    // - No acepta PDF: las páginas del PDF se mandan renderizadas como imagen (image_url con data
    //   URL base64), junto con el texto que ya sacó PdfPig o el OCR. Si el modelo configurado no
    //   acepta imágenes (la API responde 400), se reintenta una vez solo con el texto.
    // - En vez de una herramienta con esquema forzado se usa response_format = json_object, y el
    //   esquema esperado va en el prompt (la API exige pedir JSON explícitamente en el mensaje).
    //
    // La respuesta se mapea con el mismo AiExtraction.MapearFactura, así que el resultado tiene
    // exactamente la misma forma que el de Claude.
    public static class DeepSeekExtraction
    {
        static string? _apiKey;
        static string _modelo = "deepseek-flash";
        static string _baseUrl = "https://api.deepseek.com";
        static bool _enviarImagenes = true;
        static int _maxPaginas = 3;
        static readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(120) };

        // El texto de OCR de una factura rara vez pasa de unos miles de caracteres; el límite es
        // solo para que un PDF enorme no dispare el costo de la llamada.
        const int MaxCaracteresTexto = 30000;

        public static bool Disponible => !string.IsNullOrWhiteSpace(_apiKey);

        public static void Configurar(string? apiKey, string? modelo, string? baseUrl, bool enviarImagenes, int maxPaginas)
        {
            _apiKey = apiKey;
            if (!string.IsNullOrWhiteSpace(modelo)) _modelo = modelo;
            if (!string.IsNullOrWhiteSpace(baseUrl)) _baseUrl = baseUrl.TrimEnd('/');
            _enviarImagenes = enviarImagenes;
            if (maxPaginas > 0) _maxPaginas = maxPaginas;
        }

        // bytes/mediaType: el documento original (PDF o imagen). textoExtraido: lo que ya sacó
        // PdfPig u OCR, puede venir vacío (p.ej. un PDF escaneado sin texto embebido). llamadas:
        // acá se agrega cada intento (el reintento sin imágenes es otra llamada) con sus tokens.
        public static async Task<Factura?> ExtraerAsync(byte[] bytes, string mediaType, string textoExtraido, List<string> errores, List<LlamadaIa> llamadas)
        {
            if (!Disponible)
            {
                errores.Add("DeepSeek no está configurado (falta DeepSeek:ApiKey).");
                return null;
            }

            string texto = textoExtraido ?? "";
            if (texto.Length > MaxCaracteresTexto) texto = texto[..MaxCaracteresTexto];

            var imagenes = new List<string>();
            if (_enviarImagenes)
            {
                try
                {
                    imagenes = PrepararImagenes(bytes, mediaType);
                }
                catch (Exception ex)
                {
                    errores.Add("DeepSeek: no se pudieron preparar las imágenes del documento (" + ex.Message + "); se manda solo el texto.");
                }
            }

            if (imagenes.Count == 0 && string.IsNullOrWhiteSpace(texto))
            {
                errores.Add("DeepSeek: no hay ni imagen ni texto del documento para mandar.");
                return null;
            }

            var (factura, rechazoImagenes) = await LlamarAsync(imagenes, texto, errores, llamadas);
            if (factura == null && rechazoImagenes && !string.IsNullOrWhiteSpace(texto))
            {
                errores.Add($"DeepSeek rechazó las imágenes (¿el modelo {_modelo} no tiene visión?); se reintenta solo con el texto extraído.");
                (factura, _) = await LlamarAsync(new List<string>(), texto, errores, llamadas);
            }
            return factura;
        }

        // Cada página como data URL. Un PDF se renderiza a 150 dpi (legible para el modelo sin
        // mandar imágenes enormes); una foto se reorienta según EXIF y se limita a 2000 px de ancho.
        static List<string> PrepararImagenes(byte[] bytes, string mediaType)
        {
            var result = new List<string>();
            if (mediaType == "application/pdf")
            {
                int paginas = Math.Min(Conversion.GetPageCount(bytes), _maxPaginas);
                for (int i = 0; i < paginas; i++)
                {
                    var png = PaginaRender.RenderizarPdf(bytes, i, 150, SKEncodedImageFormat.Png);
                    result.Add("data:image/png;base64," + Convert.ToBase64String(png));
                }
            }
            else
            {
                var jpg = PaginaRender.RenderizarImagen(bytes, 2000, SKEncodedImageFormat.Jpeg)
                    ?? throw new InvalidOperationException("la imagen no se pudo decodificar");
                result.Add("data:image/jpeg;base64," + Convert.ToBase64String(jpg));
            }
            return result;
        }

        // Devuelve la factura, o null con el motivo en errores. rechazoImagenes = true cuando la API
        // respondió 400 con imágenes en el pedido, para que el caller reintente sin ellas.
        static async Task<(Factura? factura, bool rechazoImagenes)> LlamarAsync(List<string> imagenes, string texto, List<string> errores, List<LlamadaIa> llamadas)
        {
            var llamada = new LlamadaIa { Proveedor = IaFallback.DeepSeek, Modelo = _modelo };
            llamadas.Add(llamada);
            try
            {
                var instruccion =
                    "Esto es una factura de compra argentina que un parser automático por reglas no pudo leer bien. " +
                    "Extraé todos los datos que puedas y respondé ÚNICAMENTE con un objeto JSON válido que siga este esquema " +
                    "(JSON Schema; los nombres de las propiedades son exactamente esos):\n" +
                    AiExtraction.EsquemaFacturaJson + "\n" +
                    "Reglas: si un campo no aparece en el documento, omitilo (no inventes valores). " +
                    "Los importes van como número (no como texto), con punto como separador decimal y sin separador de miles. " +
                    "Las fechas en formato AAAA-MM-DD. Los CUIT con 11 dígitos, sin guiones.";

                JsonNode contenidoUsuario;
                string textoUsuario = string.IsNullOrWhiteSpace(texto)
                    ? "No hay texto extraído: leé los datos de las imágenes."
                    : "Texto extraído automáticamente del comprobante (puede tener errores de OCR o el orden de las columnas mezclado" +
                      (imagenes.Count > 0 ? "; ante una diferencia, vale lo que se ve en la imagen" : "") + "):\n\n" + texto;

                if (imagenes.Count == 0)
                {
                    contenidoUsuario = textoUsuario;
                }
                else
                {
                    var partes = new JsonArray();
                    foreach (var dataUrl in imagenes)
                        partes.Add(new JsonObject { ["type"] = "image_url", ["image_url"] = new JsonObject { ["url"] = dataUrl } });
                    partes.Add(new JsonObject { ["type"] = "text", ["text"] = textoUsuario });
                    contenidoUsuario = partes;
                }

                var body = new JsonObject
                {
                    ["model"] = _modelo,
                    ["max_tokens"] = 8192,
                    ["temperature"] = 0,
                    ["response_format"] = new JsonObject { ["type"] = "json_object" },
                    ["messages"] = new JsonArray
                    {
                        new JsonObject { ["role"] = "system", ["content"] = instruccion },
                        new JsonObject { ["role"] = "user", ["content"] = contenidoUsuario },
                    },
                };

                using var request = new HttpRequestMessage(HttpMethod.Post, _baseUrl + "/chat/completions");
                request.Headers.Add("Authorization", "Bearer " + _apiKey);
                request.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");

                using var response = await _http.SendAsync(request);
                string raw = await response.Content.ReadAsStringAsync();
                if (!response.IsSuccessStatusCode)
                {
                    bool rechazo = imagenes.Count > 0 && (int)response.StatusCode is 400 or 422;
                    errores.Add($"DeepSeek falló (HTTP {(int)response.StatusCode}): {Truncar(raw)}");
                    return (null, rechazo);
                }

                using var doc = JsonDocument.Parse(raw);
                // Antes de cualquier validación: una respuesta cortada o sin JSON igual se cobra.
                LeerUso(doc.RootElement, llamada);
                var choice = doc.RootElement.GetProperty("choices")[0];
                if (choice.TryGetProperty("finish_reason", out var fin) && fin.GetString() == "length")
                {
                    errores.Add("DeepSeek cortó la respuesta por límite de tokens antes de terminar el JSON.");
                    return (null, false);
                }

                string? contenido = choice.GetProperty("message").GetProperty("content").GetString();
                string? json = ExtraerObjetoJson(contenido);
                if (json == null)
                {
                    errores.Add("DeepSeek no devolvió un JSON: " + Truncar(contenido ?? ""));
                    return (null, false);
                }

                using var facturaJson = JsonDocument.Parse(json);
                var factura = AiExtraction.MapearFactura(facturaJson.RootElement);
                llamada.Exitosa = true;
                return (factura, false);
            }
            catch (Exception ex)
            {
                errores.Add("DeepSeek falló: " + ex.Message);
                return (null, false);
            }
        }

        // Con json_object la respuesta debería ser el objeto solo, pero por las dudas se tolera que
        // venga envuelto en ```json ... ``` o con texto antes/después.
        static string? ExtraerObjetoJson(string? contenido)
        {
            if (string.IsNullOrWhiteSpace(contenido)) return null;
            int ini = contenido.IndexOf('{');
            int fin = contenido.LastIndexOf('}');
            return ini >= 0 && fin > ini ? contenido[ini..(fin + 1)] : null;
        }

        // API compatible con OpenAI: prompt_tokens ya es el total de entrada, y DeepSeek informa
        // aparte cuántos fueron acierto de caché (prompt_cache_hit_tokens).
        static void LeerUso(JsonElement respuesta, LlamadaIa llamada)
        {
            llamada.LeerModelo(respuesta);
            if (!respuesta.TryGetProperty("usage", out var uso)) return;
            llamada.TokensEntrada = LlamadaIa.LeerEntero(uso, "prompt_tokens");
            llamada.TokensEntradaCache = LlamadaIa.LeerEntero(uso, "prompt_cache_hit_tokens");
            llamada.TokensSalida = LlamadaIa.LeerEntero(uso, "completion_tokens");
        }

        static string Truncar(string s) => s.Length > 300 ? s[..300] + "..." : s;
    }
}
