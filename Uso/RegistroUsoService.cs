using Microsoft.Extensions.Options;
using Soltec.DocParser.Services;
using Soltec.DocParser.Tenancy;

namespace Soltec.DocParser.Uso
{
    public interface IRegistroUsoService
    {
        Task RegistrarAsync(TenantOptions? tenant, RegistroUsoContexto uso, TimeSpan duracion, CancellationToken cancellationToken);
    }

    public sealed class RegistroUsoService : IRegistroUsoService
    {
        private readonly UsoDbContext _db;
        private readonly IOptionsMonitor<UsoOptions> _options;
        private readonly ILogger<RegistroUsoService> _logger;

        public RegistroUsoService(UsoDbContext db, IOptionsMonitor<UsoOptions> options, ILogger<RegistroUsoService> logger)
        {
            _db = db;
            _options = options;
            _logger = logger;
        }

        // Nunca tira: si la base no está disponible se loguea y el cliente recibe igual su factura.
        public async Task RegistrarAsync(TenantOptions? tenant, RegistroUsoContexto uso, TimeSpan duracion, CancellationToken cancellationToken)
        {
            try
            {
                var precios = _options.CurrentValue.PreciosIa;
                var llamadas = uso.LlamadasIa.Select(l => new RegistroUsoIa
                {
                    Proveedor = l.Proveedor,
                    Modelo = l.Modelo,
                    TokensEntrada = l.TokensEntrada,
                    TokensEntradaCache = l.TokensEntradaCache,
                    TokensSalida = l.TokensSalida,
                    CostoUsd = CalcularCosto(l, precios),
                    Exitosa = l.Exitosa,
                }).ToList();

                _db.RegistrosUso.Add(new RegistroUso
                {
                    FechaUtc = DateTime.UtcNow,
                    IdUsuario = tenant?.IdUsuario ?? 0,
                    NombreCliente = tenant?.Nombre ?? "",
                    TipoArchivo = uso.TipoArchivo,
                    NombreArchivo = uso.NombreArchivo,
                    TamanoBytes = uso.TamanoBytes,
                    Paginas = uso.Paginas,
                    Resultado = uso.Resultado,
                    UsoIa = llamadas.Count > 0,
                    ProveedorIa = uso.ProveedorIa,
                    DuracionMs = (int)Math.Min(duracion.TotalMilliseconds, int.MaxValue),
                    TokensEntrada = llamadas.Sum(l => (long)l.TokensEntrada),
                    TokensSalida = llamadas.Sum(l => (long)l.TokensSalida),
                    CostoIaUsd = llamadas.Sum(l => l.CostoUsd),
                    LlamadasIa = llamadas,
                });
                await _db.SaveChangesAsync(cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "No se pudo grabar el registro de uso del usuario {IdUsuario}", tenant?.IdUsuario);
            }
        }

        decimal CalcularCosto(LlamadaIa llamada, Dictionary<string, PrecioModeloIa> precios)
        {
            if (llamada.TokensEntrada == 0 && llamada.TokensSalida == 0) return 0m;

            var precio = BuscarPrecio(llamada.Modelo, precios);
            if (precio == null)
            {
                _logger.LogWarning("No hay precio configurado en Uso:PreciosIa para el modelo {Modelo}; el costo se registra en 0.", llamada.Modelo);
                return 0m;
            }

            int cache = Math.Min(llamada.TokensEntradaCache, llamada.TokensEntrada);
            decimal costo =
                (llamada.TokensEntrada - cache) * precio.EntradaPorMillon +
                cache * (precio.EntradaCachePorMillon ?? precio.EntradaPorMillon) +
                llamada.TokensSalida * precio.SalidaPorMillon;
            return Math.Round(costo / 1_000_000m, 8);
        }

        // Nombre exacto primero; si no, el prefijo configurado más largo que coincida.
        static PrecioModeloIa? BuscarPrecio(string modelo, Dictionary<string, PrecioModeloIa> precios)
        {
            var exacto = precios.FirstOrDefault(p => string.Equals(p.Key, modelo, StringComparison.OrdinalIgnoreCase));
            if (exacto.Value != null) return exacto.Value;

            return precios
                .Where(p => modelo.StartsWith(p.Key, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(p => p.Key.Length)
                .Select(p => p.Value)
                .FirstOrDefault();
        }
    }
}
