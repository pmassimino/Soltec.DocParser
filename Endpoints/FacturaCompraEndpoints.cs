using Soltec.DocParser.Services;
using Soltec.DocParser.Tenancy;

namespace Soltec.DocParser.Endpoints
{
    public static class FacturaCompraEndpoints
    {
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

                List<Soltec.DocParser.Services.ExtractedPage> paginas;
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
                        mensaje = "El PDF no tiene texto embebido (probablemente es una imagen escaneada). Este servicio todavía no hace OCR: cargar el comprobante manualmente.",
                    });
                }

                var primera = paginas[0];

                // ARCA/AFIP y el formato "Gravado/Exento" (AGRIPUERTO y similares) tienen una
                // estructura fija y reconocible; para cualquier otro PDF con texto (sea o no el
                // layout típico de SAE) se intenta el parser genérico, que hace lo que puede y
                // deja advertencias explícitas por cada campo que no encuentra, en vez de
                // rechazar de entrada un formato que no vio nunca.
                Soltec.DocParser.Models.Factura resultado;
                if (FacturaCompraParser.EsFormatoArca(primera.LineText))
                    resultado = FacturaCompraParser.Parse(primera.LineText, primera.Words, tenant.Cuit);
                else if (FacturaGravadoParser.EsFormatoGravado(primera.LineText))
                    resultado = FacturaGravadoParser.Parse(primera.LineText, primera.Words, tenant.Cuit);
                else if (FacturaSubTotalParser.EsFormatoSubTotal(primera.LineText))
                    resultado = FacturaSubTotalParser.Parse(primera.LineText, primera.Words, tenant.Cuit);
                else if (FacturaNetoParser.EsFormatoNeto(primera.LineText))
                    resultado = FacturaNetoParser.Parse(primera.LineText, primera.Words, tenant.Cuit);
                else
                    resultado = SaeFacturaParser.Parse(primera.LineText, primera.Words, tenant.Cuit);

                // El CTG/peso/tarifa puede venir embebido en la descripción de cualquier
                // formato (no solo el de AGRIPUERTO); se intenta siempre, sin efecto si no hay nada que extraer.
                foreach (var item in resultado.Detalle)
                    DetalleEnriquecimiento.EnriquecerConCtgPesoTarifa(item);

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

            app.MapGet("/api/health", () => Results.Ok(new { status = "ok" }))
                .AllowAnonymous();
        }
    }
}
