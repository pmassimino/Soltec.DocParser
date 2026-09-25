using Soltec.DocParser.Models;
using Soltec.DocParser.Services;
using Soltec.DocParser.Tenancy;

namespace Soltec.DocParser.Endpoints
{
    public static class FacturaCompraEndpoints
    {
        // ARCA/AFIP y los formatos "Gravado/Exento", "SUB TOTAL/TOTAL" y
        // "NETO/IVA/..." tienen una estructura fija y reconocible; para cualquier otro
        // texto (sea de un PDF o de OCR) se intenta el parser genérico, que hace lo que puede y
        // deja advertencias explícitas por cada campo que no encuentra, en vez de rechazar de
        // entrada un formato que no vio nunca. Compartido entre el endpoint de PDF y el de imagen:
        // a partir de acá (texto + palabras posicionadas) ninguno de los parsers sabe ni le
        // importa si la fuente fue un PDF con texto embebido o una imagen pasada por OCR.
        static Factura DispatchYParsear(ExtractedPage pagina)
        {
            Factura resultado;
            if (FacturaCompraParser.EsFormatoArca(pagina.LineText))
                resultado = FacturaCompraParser.Parse(pagina.LineText, pagina.Words);
            else if (FacturaGravadoParser.EsFormatoGravado(pagina.LineText))
                resultado = FacturaGravadoParser.Parse(pagina.LineText, pagina.Words);
            else if (FacturaSubTotalParser.EsFormatoSubTotal(pagina.LineText))
                resultado = FacturaSubTotalParser.Parse(pagina.LineText, pagina.Words);
            else if (FacturaNetoParser.EsFormatoNeto(pagina.LineText))
                resultado = FacturaNetoParser.Parse(pagina.LineText, pagina.Words);
            else
                resultado = SaeFacturaParser.Parse(pagina.LineText, pagina.Words);

            // El CTG/peso/tarifa puede venir embebido en la descripción de cualquier formato (no
            // solo el "Gravado/Exento"); se intenta siempre, sin efecto si no hay nada que extraer.
            foreach (var item in resultado.Detalle)
                DetalleEnriquecimiento.EnriquecerConCtgPesoTarifa(item);

            return resultado;
        }

        // Fallback opcional (ver AiExtraction): si el caller pasó ?usarIaSiFalla=true y el parser
        // por reglas no sacó ni ítems ni totales, se le manda el documento original a Claude en
        // vez de conformarse con un resultado vacío. Si la IA tampoco puede o no está configurada,
        // se devuelve el resultado del parser por reglas igual, con el motivo del fallo agregado.
        static async Task<Factura> AplicarFallbackIaSiHaceFalta(Factura resultado, bool usarIaSiFalla, byte[] bytesParaIa, string mediaType, QrData? qr)
        {
            if (!usarIaSiFalla || !AiExtraction.EsResultadoPobre(resultado))
                return resultado;

            var erroresIa = new List<string>();
            var resultadoIa = await AiExtraction.ExtraerAsync(bytesParaIa, mediaType, erroresIa);
            if (resultadoIa == null)
            {
                resultado.Advertencias.AddRange(erroresIa);
                return resultado;
            }

            foreach (var item in resultadoIa.Detalle)
                DetalleEnriquecimiento.EnriquecerConCtgPesoTarifa(item);
            QrReconciliation.Aplicar(resultadoIa, qr);
            resultadoIa.ObtenidoPorIa = true;
            resultadoIa.Advertencias.Insert(0, "El parser por reglas no pudo extraer ítems ni totales de este comprobante; este resultado viene del fallback por IA (Claude), no del parser habitual. Revisar cuidadosamente antes de usarlo.");
            return resultadoIa;
        }

        // ?formato=xml devuelve el resultado como un DataSet XML que VFP levanta con XMLAdapter
        // (ver VfpXmlSerializer); sin el parámetro, o con cualquier otro valor, sigue siendo JSON.
        static bool PideXml(HttpRequest request) =>
            string.Equals(request.Query["formato"], "xml", StringComparison.OrdinalIgnoreCase);

        static IResult Responder(HttpRequest request, Factura resultado) =>
            PideXml(request)
                ? Results.Text(VfpXmlSerializer.Serializar(resultado), "application/xml; charset=windows-1252", VfpXmlSerializer.Encoding)
                : Results.Ok(resultado);

        static IResult ResponderError(HttpRequest request, string error, string mensaje) =>
            PideXml(request)
                ? Results.Text(VfpXmlSerializer.SerializarError(error, mensaje), "application/xml; charset=windows-1252", VfpXmlSerializer.Encoding)
                : Results.Ok(new { error, mensaje });

        static int LeerEntero(HttpRequest request, string nombre, int porDefecto) =>
            int.TryParse(request.Query[nombre], out var valor) ? valor : porDefecto;

        public static void MapFacturaCompraEndpoints(this WebApplication app)
        {
            app.MapPost("/api/facturas/compra/pdf", async (HttpRequest request) =>
            {
                if (!request.HasFormContentType)
                    return Results.BadRequest("Se espera un form con un archivo PDF (multipart/form-data).");

                var form = await request.ReadFormAsync();
                var file = form.Files.FirstOrDefault();
                if (file == null || file.Length == 0)
                    return Results.BadRequest("No se recibió ningún archivo.");

                if (!file.FileName.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase) &&
                    file.ContentType != "application/pdf")
                    return Results.BadRequest("El archivo debe ser un PDF.");


                using var ms = new MemoryStream();
                await file.CopyToAsync(ms);
                var bytes = ms.ToArray();

                List<ExtractedPage> paginas;
                try
                {
                    paginas = PdfPigExtraction.ExtractDedupedPages(bytes);
                }
                catch (Exception ex)
                {
                    return Results.BadRequest("No se pudo abrir el PDF: " + ex.Message);
                }

                if (paginas.Count == 0 || paginas.All(p => string.IsNullOrWhiteSpace(p.LineText)))
                {
                    return ResponderError(request, "SIN_TEXTO_EXTRAIBLE",
                        "El PDF no tiene texto embebido (probablemente es una imagen escaneada). Probar con /api/facturas/compra/imagen, que sí hace OCR.");
                }

                var primera = paginas[0];
                var resultado = DispatchYParsear(primera);

                // El QR de AFIP/ARCA es estándar sin importar qué software generó el comprobante,
                // así que se usa como fuente principal para Numero/CUIT emisor/Fecha/Total/CAE
                // (ver QrReconciliation) en vez del texto, que varía por formato.
                var qr = QrExtraction.TryExtraerQr(bytes, paginaIndex: 0);
                QrReconciliation.Aplicar(resultado, qr);

                bool usarIaSiFalla = string.Equals(request.Query["usarIaSiFalla"], "true", StringComparison.OrdinalIgnoreCase);
                resultado = await AplicarFallbackIaSiHaceFalta(resultado, usarIaSiFalla, bytes, "application/pdf", qr);

                if (paginas.Count > 1)
                {
                    resultado.Advertencias.Add($"El PDF tiene {paginas.Count} páginas con contenido distinto entre sí; solo se procesó la primera. Revisar manualmente si hay ítems en páginas adicionales.");
                }

                return Responder(request, resultado);
            })
            .DisableAntiforgery();

            // Para fotos/escaneos de facturas en papel (sin texto embebido): usa OCR en vez de
            // PdfPig para sacar el texto y la posición de cada palabra, y de ahí en adelante corre
            // exactamente el mismo pipeline que el endpoint de PDF (mismos parsers, mismo QR,
            // mismo enriquecimiento de CTG/peso/tarifa). La calidad depende de la foto: una imagen
            // borrosa, oscura o muy chica va a dar peor resultado que un PDF nativo, sin importar
            // qué tan bien esté el parsing -por eso queda siempre marcado ObtenidoPorOcr = true,
            // para que se revise a mano antes de confiar en los datos.
            app.MapPost("/api/facturas/compra/imagen", async (HttpRequest request) =>
            {
                if (!request.HasFormContentType)
                    return Results.BadRequest("Se espera un form con una imagen (multipart/form-data).");

                var form = await request.ReadFormAsync();
                var file = form.Files.FirstOrDefault();
                if (file == null || file.Length == 0)
                    return Results.BadRequest("No se recibió ningún archivo.");

                var extensionesValidas = new[] { ".jpg", ".jpeg", ".png", ".bmp", ".webp", ".tif", ".tiff" };
                if (!extensionesValidas.Any(ext => file.FileName.EndsWith(ext, StringComparison.OrdinalIgnoreCase)))
                    return Results.BadRequest("El archivo debe ser una imagen (jpg, png, bmp, webp o tiff).");


                using var ms = new MemoryStream();
                await file.CopyToAsync(ms);
                var bytes = ms.ToArray();

                ExtractedPage pagina;
                try
                {
                    pagina = OcrExtraction.ExtraerDeImagen(bytes);
                }
                catch (Exception ex)
                {
                    return Results.BadRequest("No se pudo procesar la imagen: " + ex.Message);
                }

                if (string.IsNullOrWhiteSpace(pagina.LineText))
                {
                    return ResponderError(request, "SIN_TEXTO_RECONOCIDO",
                        "El OCR no reconoció texto en la imagen (puede estar borrosa, muy oscura o no ser una factura). Cargar el comprobante manualmente.");
                }

                var resultado = DispatchYParsear(pagina);
                resultado.ObtenidoPorOcr = true;
                resultado.Advertencias.Insert(0, "Este comprobante se leyó por OCR a partir de una imagen, no de un PDF con texto embebido: la precisión depende de la calidad de la foto. Revisar los datos antes de usarlos.");

                var qr = QrExtraction.TryExtraerQrDeImagen(bytes);
                QrReconciliation.Aplicar(resultado, qr);

                bool usarIaSiFalla = string.Equals(request.Query["usarIaSiFalla"], "true", StringComparison.OrdinalIgnoreCase);
                if (usarIaSiFalla && AiExtraction.EsResultadoPobre(resultado))
                {
                    string extension = Path.GetExtension(file.FileName);
                    var (bytesParaIa, mediaType) = AiExtraction.PrepararImagen(bytes, extension);
                    resultado = await AplicarFallbackIaSiHaceFalta(resultado, usarIaSiFalla, bytesParaIa, mediaType, qr);
                    if (resultado.ObtenidoPorIa) resultado.ObtenidoPorOcr = true;
                }

                return Responder(request, resultado);
            })
            .DisableAntiforgery();

            // Devuelve una página del comprobante (PDF o imagen) como PNG o JPG, para mostrarla al
            // lado de los datos extraídos en un cliente que no puede embeber un visor de PDF (el
            // form de revisión en VFP). No extrae nada: es independiente de /pdf e /imagen, así
            // que el cliente sube el mismo archivo otra vez. Parámetros (todos opcionales):
            //   pagina       número de página del PDF, base 1 (default 1)
            //   dpi          resolución del render del PDF, 50 a 300 (default 150)
            //   formato      "png" (default) o "jpg"
            //   anchoMaximo  para imágenes: se reduce si es más ancha, en px (default 1600)
            // El header X-Total-Paginas trae la cantidad de páginas, para paginar desde el cliente.
            app.MapPost("/api/facturas/compra/pagina", async (HttpRequest request) =>
            {
                if (!request.HasFormContentType)
                    return Results.BadRequest("Se espera un form con un archivo PDF o una imagen (multipart/form-data).");

                var form = await request.ReadFormAsync();
                var file = form.Files.FirstOrDefault();
                if (file == null || file.Length == 0)
                    return Results.BadRequest("No se recibió ningún archivo.");

                bool esJpg = string.Equals(request.Query["formato"], "jpg", StringComparison.OrdinalIgnoreCase) ||
                             string.Equals(request.Query["formato"], "jpeg", StringComparison.OrdinalIgnoreCase);
                var formato = esJpg ? SkiaSharp.SKEncodedImageFormat.Jpeg : SkiaSharp.SKEncodedImageFormat.Png;
                string contentType = esJpg ? "image/jpeg" : "image/png";

                using var ms = new MemoryStream();
                await file.CopyToAsync(ms);
                var bytes = ms.ToArray();

                bool esPdf = file.FileName.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase) || file.ContentType == "application/pdf";
                if (!esPdf)
                {
                    int anchoMaximo = Math.Clamp(LeerEntero(request, "anchoMaximo", 1600), 200, 6000);
                    byte[]? imagen;
                    try
                    {
                        imagen = PaginaRender.RenderizarImagen(bytes, anchoMaximo, formato);
                    }
                    catch (Exception ex)
                    {
                        return Results.BadRequest("No se pudo procesar la imagen: " + ex.Message);
                    }
                    if (imagen == null)
                        return Results.BadRequest("El archivo no es un PDF ni una imagen reconocible (jpg, png, bmp, webp).");

                    request.HttpContext.Response.Headers["X-Total-Paginas"] = "1";
                    return Results.File(imagen, contentType);
                }

                int totalPaginas;
                try
                {
                    totalPaginas = PaginaRender.ContarPaginasPdf(bytes);
                }
                catch (Exception ex)
                {
                    return Results.BadRequest("No se pudo abrir el PDF: " + ex.Message);
                }

                int pagina = LeerEntero(request, "pagina", 1);
                if (pagina < 1 || pagina > totalPaginas)
                    return Results.BadRequest($"La página {pagina} no existe; el PDF tiene {totalPaginas}.");

                int dpi = Math.Clamp(LeerEntero(request, "dpi", 150), PaginaRender.DpiMinimo, PaginaRender.DpiMaximo);

                var imagenPdf = PaginaRender.RenderizarPdf(bytes, pagina - 1, dpi, formato);
                request.HttpContext.Response.Headers["X-Total-Paginas"] = totalPaginas.ToString();
                return Results.File(imagenPdf, contentType);
            })
            .DisableAntiforgery();

            app.MapGet("/api/health", () => Results.Ok(new { status = "ok" }))
                .AllowAnonymous();
        }
    }
}
