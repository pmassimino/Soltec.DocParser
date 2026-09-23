using Soltec.DocParser.Models;
using Soltec.DocParser.Services;
using Soltec.DocParser.Tenancy;

namespace Soltec.DocParser.Endpoints
{
    public static class FacturaCompraEndpoints
    {
        // ARCA/AFIP y los formatos "Gravado/Exento" (AGRIPUERTO), "SUB TOTAL/TOTAL" (APSA) y
        // "NETO/IVA/..." (Atiseni) tienen una estructura fija y reconocible; para cualquier otro
        // texto (sea de un PDF o de OCR) se intenta el parser genérico, que hace lo que puede y
        // deja advertencias explícitas por cada campo que no encuentra, en vez de rechazar de
        // entrada un formato que no vio nunca. Compartido entre el endpoint de PDF y el de imagen:
        // a partir de acá (texto + palabras posicionadas) ninguno de los parsers sabe ni le
        // importa si la fuente fue un PDF con texto embebido o una imagen pasada por OCR.
        static Factura DispatchYParsear(ExtractedPage pagina, string empresaCuit)
        {
            Factura resultado;
            if (FacturaCompraParser.EsFormatoArca(pagina.LineText))
                resultado = FacturaCompraParser.Parse(pagina.LineText, pagina.Words, empresaCuit);
            else if (FacturaGravadoParser.EsFormatoGravado(pagina.LineText))
                resultado = FacturaGravadoParser.Parse(pagina.LineText, pagina.Words, empresaCuit);
            else if (FacturaSubTotalParser.EsFormatoSubTotal(pagina.LineText))
                resultado = FacturaSubTotalParser.Parse(pagina.LineText, pagina.Words, empresaCuit);
            else if (FacturaNetoParser.EsFormatoNeto(pagina.LineText))
                resultado = FacturaNetoParser.Parse(pagina.LineText, pagina.Words, empresaCuit);
            else
                resultado = SaeFacturaParser.Parse(pagina.LineText, pagina.Words, empresaCuit);

            // El CTG/peso/tarifa puede venir embebido en la descripción de cualquier formato (no
            // solo el de AGRIPUERTO); se intenta siempre, sin efecto si no hay nada que extraer.
            foreach (var item in resultado.Detalle)
                DetalleEnriquecimiento.EnriquecerConCtgPesoTarifa(item);

            return resultado;
        }

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

                var tenant = request.HttpContext.GetTenant();

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
                    return Results.Ok(new
                    {
                        error = "SIN_TEXTO_EXTRAIBLE",
                        mensaje = "El PDF no tiene texto embebido (probablemente es una imagen escaneada). Probar con /api/facturas/compra/imagen, que sí hace OCR.",
                    });
                }

                var primera = paginas[0];
                var resultado = DispatchYParsear(primera, tenant.Cuit);

                // El QR de AFIP/ARCA es estándar sin importar qué software generó el comprobante,
                // así que se usa como fuente principal para Numero/CUIT emisor/Fecha/Total/CAE
                // (ver QrReconciliation) en vez del texto, que varía por formato.
                var qr = QrExtraction.TryExtraerQr(bytes, paginaIndex: 0);
                QrReconciliation.Aplicar(resultado, qr);
                QrReconciliation.DecidirEsCompra(resultado, tenant.Cuit);

                if (paginas.Count > 1)
                {
                    resultado.Advertencias.Add($"El PDF tiene {paginas.Count} páginas con contenido distinto entre sí; solo se procesó la primera. Revisar manualmente si hay ítems en páginas adicionales.");
                }

                return Results.Ok(resultado);
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

                var tenant = request.HttpContext.GetTenant();

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
                    return Results.Ok(new
                    {
                        error = "SIN_TEXTO_RECONOCIDO",
                        mensaje = "El OCR no reconoció texto en la imagen (puede estar borrosa, muy oscura o no ser una factura). Cargar el comprobante manualmente.",
                    });
                }

                var resultado = DispatchYParsear(pagina, tenant.Cuit);
                resultado.ObtenidoPorOcr = true;
                resultado.Advertencias.Insert(0, "Este comprobante se leyó por OCR a partir de una imagen, no de un PDF con texto embebido: la precisión depende de la calidad de la foto. Revisar los datos antes de usarlos.");

                var qr = QrExtraction.TryExtraerQrDeImagen(bytes);
                QrReconciliation.Aplicar(resultado, qr);
                QrReconciliation.DecidirEsCompra(resultado, tenant.Cuit);

                return Results.Ok(resultado);
            })
            .DisableAntiforgery();

            app.MapGet("/api/health", () => Results.Ok(new { status = "ok" }))
                .AllowAnonymous();
        }
    }
}
