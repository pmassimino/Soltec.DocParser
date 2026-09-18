using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Options;

namespace Soltec.DocParser.Tenancy
{
    public class TenantApiKeyMiddleware
    {
        public const string ApiKeyHeaderName = "ApiKey";
        public const string TenantItemKey = "Tenant";

        private readonly RequestDelegate _next;

        public TenantApiKeyMiddleware(RequestDelegate next)
        {
            _next = next;
        }

        public async Task InvokeAsync(HttpContext context, IOptionsMonitor<List<TenantOptions>> tenantsMonitor)
        {
            if (context.Request.Method == "OPTIONS")
            {
                await _next(context);
                return;
            }

            var endpoint = context.GetEndpoint();
            var isAllowAnonymous = endpoint?.Metadata.Any(m => m is AllowAnonymousAttribute) ?? false;
            if (isAllowAnonymous)
            {
                await _next(context);
                return;
            }

            if (!context.Request.Headers.TryGetValue(ApiKeyHeaderName, out var apiKey) || string.IsNullOrEmpty(apiKey))
            {
                context.Response.StatusCode = 401;
                await context.Response.WriteAsync("API Key was not provided");
                return;
            }

            var tenant = tenantsMonitor.CurrentValue.FirstOrDefault(t =>
                !string.IsNullOrEmpty(t.ApiKey) &&
                t.ApiKey.Length == apiKey.ToString().Length &&
                CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(t.ApiKey), Encoding.UTF8.GetBytes(apiKey.ToString())));

            if (tenant == null)
            {
                context.Response.StatusCode = 401;
                await context.Response.WriteAsync("Unauthorized client");
                return;
            }

            context.Items[TenantItemKey] = tenant;
            await _next(context);
        }
    }

    public static class TenantHttpContextExtensions
    {
        public static TenantOptions GetTenant(this HttpContext context)
        {
            return context.Items[TenantApiKeyMiddleware.TenantItemKey] as TenantOptions
                ?? throw new InvalidOperationException("No hay tenant resuelto para este request. ¿Falta el middleware de ApiKey?");
        }
    }
}
