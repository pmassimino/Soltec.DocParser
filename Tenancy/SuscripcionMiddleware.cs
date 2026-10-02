using System.Net.Http.Headers;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;

namespace Soltec.DocParser.Tenancy
{
    // Corre después de UseAuthorization: a esta altura el JWT ya fue validado (firma, issuer,
    // audience, vencimiento). Acá solo se verifica contra Soltec.Suscripcion que la cuenta del
    // usuario tenga la suscripción vigente, y se deja el Tenant resuelto para el resto del request.
    public class SuscripcionMiddleware
    {
        public const string TenantItemKey = "Tenant";
        static readonly string[] EstadosVigentes = { "ACTIVO", "AVISO" };

        private readonly RequestDelegate _next;

        public SuscripcionMiddleware(RequestDelegate next)
        {
            _next = next;
        }

        public async Task InvokeAsync(HttpContext context, IOptionsMonitor<SuscripcionOptions> options, IHttpClientFactory httpClientFactory, IMemoryCache cache, ILogger<SuscripcionMiddleware> logger)
        {
            var endpoint = context.GetEndpoint();
            var isAllowAnonymous = endpoint?.Metadata.Any(m => m is IAllowAnonymous) ?? false;
            if (context.Request.Method == "OPTIONS" || isAllowAnonymous || context.User.Identity?.IsAuthenticated != true)
            {
                await _next(context);
                return;
            }

            int.TryParse(context.User.FindFirst("idUsuario")?.Value, out var idUsuario);
            var opciones = options.CurrentValue;

            // El estado se cachea por usuario para no ir a Suscripcion en cada factura; una
            // suspensión tarda como máximo CacheMinutos en hacerse efectiva.
            var cacheKey = $"suscripcion-vigente:{idUsuario}";
            if (!cache.TryGetValue(cacheKey, out bool vigente))
            {
                List<EstadoSuscripcion>? estados;
                try
                {
                    var client = httpClientFactory.CreateClient(nameof(SuscripcionMiddleware));
                    using var request = new HttpRequestMessage(HttpMethod.Get, opciones.UrlService.TrimEnd('/') + "/api/suscripcion/estado/plan");
                    request.Headers.Authorization = AuthenticationHeaderValue.Parse(context.Request.Headers.Authorization.ToString());
                    using var response = await client.SendAsync(request, context.RequestAborted);
                    // 400 = el usuario no tiene cuentas asignadas: no es un error del servicio.
                    estados = response.IsSuccessStatusCode
                        ? await response.Content.ReadFromJsonAsync<List<EstadoSuscripcion>>(cancellationToken: context.RequestAborted)
                        : response.StatusCode == System.Net.HttpStatusCode.BadRequest ? new() : null;
                }
                catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException)
                {
                    logger.LogError(ex, "No se pudo consultar Soltec.Suscripcion");
                    estados = null;
                }

                if (estados == null)
                {
                    context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
                    await context.Response.WriteAsync("No se pudo verificar la suscripción");
                    return;
                }

                vigente = estados.Any(e =>
                    EstadosVigentes.Contains(e.Estado, StringComparer.OrdinalIgnoreCase) &&
                    (opciones.IdPlanRequerido == 0 || e.IdPlan == opciones.IdPlanRequerido));
                cache.Set(cacheKey, vigente, TimeSpan.FromMinutes(opciones.CacheMinutos));
            }

            if (!vigente)
            {
                context.Response.StatusCode = StatusCodes.Status403Forbidden;
                await context.Response.WriteAsync("Suscripción no vigente");
                return;
            }

            context.Items[TenantItemKey] = new TenantOptions
            {
                IdUsuario = idUsuario,
                Nombre = context.User.FindFirst("name")?.Value ?? ""
            };
            await _next(context);
        }

        // Respuesta de GET /api/suscripcion/estado/plan de Soltec.Suscripcion.
        class EstadoSuscripcion
        {
            public int IdPlan { get; set; }
            public string NombrePlan { get; set; } = "";
            public string Estado { get; set; } = "";
        }
    }

    public static class TenantHttpContextExtensions
    {
        public static TenantOptions GetTenant(this HttpContext context)
        {
            return context.Items[SuscripcionMiddleware.TenantItemKey] as TenantOptions
                ?? throw new InvalidOperationException("No hay tenant resuelto para este request. ¿Falta el middleware de Suscripcion?");
        }
    }
}
