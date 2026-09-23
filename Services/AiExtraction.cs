using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using SkiaSharp;
using Soltec.DocParser.Models;

namespace Soltec.DocParser.Services
{
    // Fallback opcional al parser por reglas: cuando el caller lo pide explícitamente (parámetro
    // ?usarIaSiFalla=true en los endpoints de /pdf y /imagen) y el parser por reglas no logró
    // sacar ni ítems ni totales, se le manda el documento original (PDF o imagen, con visión, no
    // el texto ya extraído) directamente a la API de Claude para que intente la misma extracción.
    // Es deliberadamente opt-in y solo se dispara ante un resultado pobre: el parser por reglas es
    // gratis, instantáneo y cubre la gran mayoría de los casos reales -esto es un último recurso
    // para el resto, pagando una llamada a una API externa por cada intento.
    public static class AiExtraction
    {
        static string? _apiKey;
        static string _modelo = "claude-sonnet-5";
        static readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(90) };

        public static bool Disponible => !string.IsNullOrWhiteSpace(_apiKey);

        public static void Configurar(string? apiKey, string? modelo)
        {
            _apiKey = apiKey;
            if (!string.IsNullOrWhiteSpace(modelo)) _modelo = modelo;
        }

        // Un resultado del parser por reglas se considera "pobre" cuando no hay ítems ni ningún
        // total (subtotal, neto gravado o IVA): sin eso no hay nada rescatable para una
        // registración contable, sea cual sea el resto de los campos que sí se sacaron.
        public static bool EsResultadoPobre(Factura f) =>
            f.Detalle.Count == 0 && f.Subtotal == 0 && f.ImporteNetoGravado == 0 && f.Ivas.Count == 0;

        // Claude solo acepta image/jpeg, image/png, image/gif o image/webp -los otros formatos
        // que el endpoint de imagen admite (bmp, tiff) se re-codifican a PNG antes de mandarlos.
        public static (byte[] bytes, string mediaType) PrepararImagen(byte[] bytes, string extension)
        {
            var soportados = new Dictionary<string, string>
            {
                [".jpg"] = "image/jpeg",
                [".jpeg"] = "image/jpeg",
                [".png"] = "image/png",
                [".webp"] = "image/webp",
            };
            if (soportados.TryGetValue(extension.ToLowerInvariant(), out var mediaType))
                return (bytes, mediaType);

            using var bitmap = SKBitmap.Decode(bytes);
            using var image = SKImage.FromBitmap(bitmap);
            using var data = image.Encode(SKEncodedImageFormat.Png, 100);
            return (data.ToArray(), "image/png");
        }

        public static async Task<Factura?> ExtraerAsync(byte[] bytes, string mediaType, List<string> erroresIa)
        {
            if (!Disponible)
            {
                erroresIa.Add("El fallback por IA no está configurado (falta la API key de Anthropic).");
                return null;
            }

            try
            {
                bool esPdf = mediaType == "application/pdf";
                var contenido = new JsonArray
                {
                    new JsonObject
                    {
                        ["type"] = esPdf ? "document" : "image",
                        ["source"] = new JsonObject
                        {
                            ["type"] = "base64",
                            ["media_type"] = mediaType,
                            ["data"] = Convert.ToBase64String(bytes),
                        },
                    },
                    new JsonObject
                    {
                        ["type"] = "text",
                        ["text"] = "Esto es una factura de compra argentina que un parser automático por reglas no pudo leer bien. " +
                            "Extraé todos los datos que puedas usando la herramienta extraer_factura. " +
                            "Si un campo no aparece en el documento, omitilo (no inventes valores). " +
                            "Los importes van como número (no como texto), con punto como separador decimal. Las fechas en formato AAAA-MM-DD.",
                    },
                };

                var body = new JsonObject
                {
                    ["model"] = _modelo,
                    ["max_tokens"] = 4096,
                    ["tools"] = new JsonArray { JsonNode.Parse(ToolSchemaJson) },
                    ["tool_choice"] = new JsonObject { ["type"] = "tool", ["name"] = "extraer_factura" },
                    ["messages"] = new JsonArray
                    {
                        new JsonObject { ["role"] = "user", ["content"] = contenido },
                    },
                };

                using var request = new HttpRequestMessage(HttpMethod.Post, "https://api.anthropic.com/v1/messages");
                request.Headers.Add("x-api-key", _apiKey);
                request.Headers.Add("anthropic-version", "2023-06-01");
                if (esPdf) request.Headers.Add("anthropic-beta", "pdfs-2024-09-25");
                request.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");

                using var response = await _http.SendAsync(request);
                string raw = await response.Content.ReadAsStringAsync();
                if (!response.IsSuccessStatusCode)
                {
                    erroresIa.Add($"El fallback por IA falló (HTTP {(int)response.StatusCode}): {Truncar(raw)}");
                    return null;
                }

                using var doc = JsonDocument.Parse(raw);
                var bloques = doc.RootElement.GetProperty("content").EnumerateArray();
                var toolUse = bloques.FirstOrDefault(b => b.GetProperty("type").GetString() == "tool_use");
                if (toolUse.ValueKind != JsonValueKind.Object)
                {
                    erroresIa.Add("El fallback por IA no devolvió una extracción estructurada.");
                    return null;
                }

                return MapearFactura(toolUse.GetProperty("input"));
            }
            catch (Exception ex)
            {
                erroresIa.Add("El fallback por IA falló: " + ex.Message);
                return null;
            }
        }

        static string Truncar(string s) => s.Length > 300 ? s[..300] + "..." : s;

        static string? Str(JsonElement e, string prop) =>
            e.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

        static decimal Dec(JsonElement e, string prop)
        {
            if (!e.TryGetProperty(prop, out var v)) return 0m;
            if (v.ValueKind == JsonValueKind.Number && v.TryGetDecimal(out var d)) return d;
            if (v.ValueKind == JsonValueKind.String && decimal.TryParse(v.GetString(), NumberStyles.Any, CultureInfo.InvariantCulture, out var d2)) return d2;
            return 0m;
        }

        static decimal? DecNullable(JsonElement e, string prop) => e.TryGetProperty(prop, out _) ? Dec(e, prop) : null;

        static DateTime? Fecha(JsonElement e, string prop)
        {
            var s = Str(e, prop);
            return s != null && DateTime.TryParseExact(s, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? d : null;
        }

        static List<ImporteConId> Lista(JsonElement e, string prop)
        {
            var result = new List<ImporteConId>();
            if (!e.TryGetProperty(prop, out var arr) || arr.ValueKind != JsonValueKind.Array) return result;
            foreach (var item in arr.EnumerateArray())
            {
                var id = Str(item, "id");
                if (string.IsNullOrWhiteSpace(id)) continue;
                result.Add(new ImporteConId { Id = id, Importe = Dec(item, "importe") });
            }
            return result;
        }

        static Factura MapearFactura(JsonElement input)
        {
            var f = new Factura
            {
                TipoComprobante = Str(input, "tipoComprobante") ?? "",
                CodigoComprobante = Str(input, "codigoComprobante") ?? "",
                Letra = Str(input, "letra") ?? "",
                PuntoVenta = Str(input, "puntoVenta") ?? "",
                Numero = Str(input, "numero") ?? "",
                FechaEmision = Fecha(input, "fechaEmision"),
                FechaVencimientoPago = Fecha(input, "fechaVencimientoPago"),
                PeriodoFacturadoDesde = Fecha(input, "periodoFacturadoDesde"),
                PeriodoFacturadoHasta = Fecha(input, "periodoFacturadoHasta"),
                CondicionVenta = Str(input, "condicionVenta") ?? "",
                ProveedorCuit = Str(input, "proveedorCuit") ?? "",
                ProveedorRazonSocial = Str(input, "proveedorRazonSocial") ?? "",
                ProveedorDomicilio = Str(input, "proveedorDomicilio") ?? "",
                ProveedorCondicionIva = Str(input, "proveedorCondicionIva") ?? "",
                ProveedorIngresosBrutos = Str(input, "proveedorIngresosBrutos") ?? "",
                ReceptorCuit = Str(input, "receptorCuit") ?? "",
                ReceptorRazonSocial = Str(input, "receptorRazonSocial") ?? "",
                Subtotal = Dec(input, "subtotal"),
                ImporteNetoGravado = Dec(input, "importeNetoGravado"),
                Ivas = Lista(input, "ivas"),
                OtrosTributos = Lista(input, "otrosTributos"),
                ImporteTotal = Dec(input, "importeTotal"),
                Cae = Str(input, "cae") ?? "",
                FechaVtoCae = Fecha(input, "fechaVtoCae"),
            };

            if (input.TryGetProperty("detalle", out var detalleArr) && detalleArr.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in detalleArr.EnumerateArray())
                {
                    f.Detalle.Add(new DetalleFactura
                    {
                        Codigo = Str(item, "codigo") ?? "",
                        Concepto = Str(item, "concepto") ?? "",
                        Cantidad = Dec(item, "cantidad"),
                        UnidadMedida = Str(item, "unidadMedida") ?? "",
                        PrecioUnitario = Dec(item, "precioUnitario"),
                        PorcentajeBonificacion = Dec(item, "porcentajeBonificacion"),
                        ImporteBonificacion = Dec(item, "importeBonificacion"),
                        Subtotal = Dec(item, "subtotal"),
                        AlicuotaIva = Dec(item, "alicuotaIva"),
                        SubtotalConIva = Dec(item, "subtotalConIva"),
                        Ctg = Str(item, "ctg") ?? "",
                        Peso = DecNullable(item, "peso"),
                        Tarifa = DecNullable(item, "tarifa"),
                    });
                }
            }

            return f;
        }

        const string ToolSchemaJson = """
        {
          "name": "extraer_factura",
          "description": "Extrae los datos estructurados de una factura de compra argentina (encabezado, totales e items).",
          "input_schema": {
            "type": "object",
            "properties": {
              "tipoComprobante": { "type": "string", "description": "FACTURA, NOTA DE CREDITO o NOTA DE DEBITO" },
              "codigoComprobante": { "type": "string", "description": "Código AFIP de 2-3 dígitos del tipo de comprobante" },
              "letra": { "type": "string", "description": "A, B, C o M" },
              "puntoVenta": { "type": "string" },
              "numero": { "type": "string" },
              "fechaEmision": { "type": "string", "description": "AAAA-MM-DD" },
              "fechaVencimientoPago": { "type": "string", "description": "AAAA-MM-DD" },
              "periodoFacturadoDesde": { "type": "string", "description": "AAAA-MM-DD" },
              "periodoFacturadoHasta": { "type": "string", "description": "AAAA-MM-DD" },
              "condicionVenta": { "type": "string", "description": "Contado, Cuenta Corriente, Cheque, etc." },
              "proveedorCuit": { "type": "string", "description": "CUIT del emisor, 11 dígitos sin guiones" },
              "proveedorRazonSocial": { "type": "string" },
              "proveedorDomicilio": { "type": "string" },
              "proveedorCondicionIva": { "type": "string" },
              "proveedorIngresosBrutos": { "type": "string" },
              "receptorCuit": { "type": "string", "description": "CUIT del receptor/cliente, 11 dígitos sin guiones" },
              "receptorRazonSocial": { "type": "string" },
              "subtotal": { "type": "number" },
              "importeNetoGravado": { "type": "number" },
              "ivas": {
                "type": "array",
                "description": "Una entrada por cada alícuota de IVA discriminada en el comprobante",
                "items": {
                  "type": "object",
                  "properties": {
                    "id": { "type": "string", "description": "IVA21, IVA105, IVA27, IVA5, IVA2_5 o IVA0" },
                    "importe": { "type": "number" }
                  },
                  "required": ["id", "importe"]
                }
              },
              "otrosTributos": {
                "type": "array",
                "description": "Percepciones/impuestos que no son IVA (Percepción IB, Percepción IVA, Impuesto Interno, No Gravado, etc.)",
                "items": {
                  "type": "object",
                  "properties": {
                    "id": { "type": "string", "description": "Nombre del tributo tal como figura en el comprobante" },
                    "importe": { "type": "number" }
                  },
                  "required": ["id", "importe"]
                }
              },
              "importeTotal": { "type": "number" },
              "cae": { "type": "string" },
              "fechaVtoCae": { "type": "string", "description": "AAAA-MM-DD" },
              "detalle": {
                "type": "array",
                "description": "Ítems/renglones de la factura",
                "items": {
                  "type": "object",
                  "properties": {
                    "codigo": { "type": "string" },
                    "concepto": { "type": "string", "description": "Descripción del ítem, incluyendo CTG/carta de porte si la menciona" },
                    "cantidad": { "type": "number" },
                    "unidadMedida": { "type": "string" },
                    "precioUnitario": { "type": "number" },
                    "porcentajeBonificacion": { "type": "number" },
                    "importeBonificacion": { "type": "number" },
                    "subtotal": { "type": "number" },
                    "alicuotaIva": { "type": "number", "description": "Porcentaje de IVA del ítem, p.ej 21" },
                    "subtotalConIva": { "type": "number" },
                    "ctg": { "type": "string", "description": "Código de Trazabilidad de Granos: siempre 11 dígitos, sin ceros a la izquierda ni guiones" },
                    "peso": { "type": "number" },
                    "tarifa": { "type": "number" }
                  }
                }
              }
            }
          }
        }
        """;
    }
}
