using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;

namespace Soltec.DocParser.Uso
{
    // Estadísticas de uso para el administrador del servicio (no para los clientes): carga,
    // frecuencia de uso por cliente, cuánto se delega a la IA y cuánto cuesta. No usa el JWT de
    // Soltec.Suscripcion sino una clave propia (Uso:ApiKey) en el header ApiKey.
    public static class UsoAdminEndpoints
    {
        const string HeaderApiKey = "ApiKey";
        const int MaxDiasRango = 366;

        public static void MapUsoAdminEndpoints(this WebApplication app)
        {
            // ?desde=AAAA-MM-DD&hasta=AAAA-MM-DD, ambas inclusive y opcionales (default: los
            // últimos 30 días hasta hoy).
            app.MapGet("/api/admin/uso", async (DateOnly? desde, DateOnly? hasta, IUsoEstadisticasService estadisticas, CancellationToken cancellationToken) =>
            {
                var hastaEfectivo = hasta ?? DateOnly.FromDateTime(DateTime.Now);
                var desdeEfectivo = desde ?? hastaEfectivo.AddDays(-29);

                if (desdeEfectivo > hastaEfectivo)
                    return Results.ValidationProblem(new Dictionary<string, string[]> { ["desde"] = new[] { "'desde' no puede ser posterior a 'hasta'." } });
                if (hastaEfectivo.DayNumber - desdeEfectivo.DayNumber + 1 > MaxDiasRango)
                    return Results.ValidationProblem(new Dictionary<string, string[]> { ["desde"] = new[] { $"El rango no puede superar {MaxDiasRango} días." } });

                return Results.Ok(await estadisticas.ObtenerAsync(desdeEfectivo, hastaEfectivo, cancellationToken));
            })
            .AllowAnonymous()
            .AddEndpointFilter<ApiKeyFilter>();
        }

        sealed class ApiKeyFilter : IEndpointFilter
        {
            private readonly IOptionsMonitor<UsoOptions> _options;

            public ApiKeyFilter(IOptionsMonitor<UsoOptions> options)
            {
                _options = options;
            }

            public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
            {
                string esperada = _options.CurrentValue.ApiKey;
                // Sin clave configurada el endpoint no existe, en vez de quedar abierto.
                if (string.IsNullOrEmpty(esperada))
                    return Results.NotFound();

                string recibida = context.HttpContext.Request.Headers[HeaderApiKey].ToString();
                if (!CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(recibida), Encoding.UTF8.GetBytes(esperada)))
                    return Results.Problem(statusCode: StatusCodes.Status401Unauthorized, title: $"Falta o es incorrecto el header {HeaderApiKey}.");

                return await next(context);
            }
        }
    }
}
