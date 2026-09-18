using Soltec.FacturaParser.Services;
using Soltec.FacturaParser.Tenancy;

namespace Soltec.FacturaParser.Endpoints
{
    public static class FacturaParserEndpoints
    {
        public static void MapFacturaParserEndpoints(this WebApplication app)
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

                var extraccion = PdfTextExtractionService.ExtractText(bytes);

                if (!extraccion.TieneTexto)
                {
                    return Results.Ok(new
                    {
                        error = "SIN_TEXTO_EXTRAIBLE",
                        mensaje = "El PDF no tiene texto embebido (probablemente es una imagen escaneada). Este servicio todavía no hace OCR: cargar el comprobante manualmente.",
                    });
                }

                if (extraccion.PaginasUnicas.Count > 1)
                {
                    // Puede ser paginación real (más ítems) o simplemente un formato no reconocido;
                    // se avisa en vez de asumir cualquiera de los dos.
                }

                var resultado = FacturaCompraParser.Parse(extraccion.PaginasUnicas[0], tenant.Cuit);
                if (extraccion.PaginasUnicas.Count > 1)
                {
                    resultado.Advertencias.Add($"El PDF tiene {extraccion.PaginasUnicas.Count} páginas con contenido distinto entre sí; solo se procesó la primera. Revisar manualmente si hay ítems en páginas adicionales.");
                }

                return Results.Ok(resultado);
            })
            .DisableAntiforgery();

            app.MapGet("/api/health", () => Results.Ok(new { status = "ok" }))
                .AllowAnonymous();
        }
    }
}
