using System.Diagnostics;
using Soltec.DocParser.Services;
using Soltec.DocParser.Tenancy;

namespace Soltec.DocParser.Uso
{
    // Lo que el endpoint va anotando mientras procesa un comprobante; RegistroUsoFilter lo crea
    // al empezar el request y lo graba al terminar, salga como salga.
    public sealed class RegistroUsoContexto
    {
        public const string ItemKey = "RegistroUso";
        public const string ResultadoOk = "OK";

        public string TipoArchivo { get; set; } = "";
        public string? NombreArchivo { get; set; }
        public long TamanoBytes { get; set; }
        public int? Paginas { get; set; }
        public string Resultado { get; set; } = ResultadoOk;
        public string? ProveedorIa { get; set; }
        public List<LlamadaIa> LlamadasIa { get; } = new();
    }

    public static class RegistroUsoHttpContextExtensions
    {
        // Sin el filtro (p.ej. en un test que llama al handler directo) devuelve un contexto
        // descartable, para que el endpoint no tenga que chequear null en cada anotación.
        public static RegistroUsoContexto GetRegistroUso(this HttpContext context) =>
            context.Items[RegistroUsoContexto.ItemKey] as RegistroUsoContexto ?? new RegistroUsoContexto();

        public static RouteHandlerBuilder RegistrarUso(this RouteHandlerBuilder builder) =>
            builder.AddEndpointFilter<RegistroUsoFilter>();
    }

    public sealed class RegistroUsoFilter : IEndpointFilter
    {
        private readonly IRegistroUsoService _registroUso;

        public RegistroUsoFilter(IRegistroUsoService registroUso)
        {
            _registroUso = registroUso;
        }

        public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
        {
            var httpContext = context.HttpContext;
            var uso = new RegistroUsoContexto();
            httpContext.Items[RegistroUsoContexto.ItemKey] = uso;
            var cronometro = Stopwatch.StartNew();
            try
            {
                var resultado = await next(context);
                // Los rechazos de validación (BadRequest) no pasan por ResponderError: se toma el
                // status code, salvo que el endpoint ya haya anotado un error más específico.
                if (resultado is IStatusCodeHttpResult { StatusCode: >= 400 } conError && uso.Resultado == RegistroUsoContexto.ResultadoOk)
                    uso.Resultado = $"HTTP_{conError.StatusCode}";
                return resultado;
            }
            catch
            {
                uso.Resultado = "EXCEPCION";
                throw;
            }
            finally
            {
                cronometro.Stop();
                var tenant = httpContext.Items[SuscripcionMiddleware.TenantItemKey] as TenantOptions;
                // CancellationToken.None a propósito: si el cliente cortó la conexión, el
                // procesamiento (y lo gastado en IA) ya ocurrió y tiene que quedar registrado.
                await _registroUso.RegistrarAsync(tenant, uso, cronometro.Elapsed, CancellationToken.None);
            }
        }
    }
}
