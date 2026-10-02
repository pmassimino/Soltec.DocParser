using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Soltec.DocParser.Uso
{
    public sealed record EstadisticasUso(
        DateOnly Desde,
        DateOnly Hasta,
        string ZonaHoraria,
        ResumenUso Totales,
        IReadOnlyList<UsoPorCliente> PorCliente,
        IReadOnlyList<UsoPorProveedorIa> PorProveedorIa,
        IReadOnlyList<UsoPorDia> PorDia,
        IReadOnlyList<UsoPorHora> PorHora,
        IReadOnlyList<UsoPorResultado> PorResultado);

    public sealed record ResumenUso(
        int Solicitudes,
        int Procesadas,
        int ConError,
        int Pdf,
        int Imagenes,
        int DelegadasAIa,
        int ResueltasPorIa,
        long TokensEntrada,
        long TokensSalida,
        decimal CostoIaUsd,
        int ClientesActivos,
        double PromedioSolicitudesPorDia,
        int DuracionPromedioMs,
        int DuracionP95Ms);

    public sealed record UsoPorCliente(
        int IdUsuario,
        string Nombre,
        int Solicitudes,
        int Procesadas,
        int Pdf,
        int Imagenes,
        int DelegadasAIa,
        long TokensEntrada,
        long TokensSalida,
        decimal CostoIaUsd,
        int DiasActivos,
        double PromedioPorDiaActivo,
        DateTime PrimerUso,
        DateTime UltimoUso);

    public sealed record UsoPorProveedorIa(
        string Proveedor,
        string Modelo,
        int Llamadas,
        int Exitosas,
        long TokensEntrada,
        long TokensSalida,
        decimal CostoUsd);

    public sealed record UsoPorDia(DateOnly Fecha, int Solicitudes, int DelegadasAIa, decimal CostoIaUsd);

    public sealed record UsoPorHora(int Hora, int Solicitudes);

    public sealed record UsoPorResultado(string Resultado, int Cantidad);

    public interface IUsoEstadisticasService
    {
        Task<EstadisticasUso> ObtenerAsync(DateOnly desde, DateOnly hasta, CancellationToken cancellationToken);
    }

    public sealed class UsoEstadisticasService : IUsoEstadisticasService
    {
        private readonly UsoDbContext _db;
        private readonly TimeZoneInfo _zona;

        public UsoEstadisticasService(UsoDbContext db, IOptions<UsoOptions> options)
        {
            _db = db;
            _zona = TimeZoneInfo.TryFindSystemTimeZoneById(options.Value.ZonaHoraria, out var zona) ? zona : TimeZoneInfo.Utc;
        }

        // desde/hasta son fechas locales (en ZonaHoraria), ambas inclusive. Las filas del rango se
        // traen a memoria y se agregan acá: SQLite no sabe sumar decimal ni agrupar por día en la
        // zona horaria local, y el volumen de un período razonable es chico.
        public async Task<EstadisticasUso> ObtenerAsync(DateOnly desde, DateOnly hasta, CancellationToken cancellationToken)
        {
            var desdeUtc = TimeZoneInfo.ConvertTimeToUtc(desde.ToDateTime(TimeOnly.MinValue), _zona);
            var hastaUtc = TimeZoneInfo.ConvertTimeToUtc(hasta.AddDays(1).ToDateTime(TimeOnly.MinValue), _zona);

            var enRango = _db.RegistrosUso.AsNoTracking().Where(r => r.FechaUtc >= desdeUtc && r.FechaUtc < hastaUtc);

            var registros = await enRango
                .Select(r => new
                {
                    r.FechaUtc,
                    r.IdUsuario,
                    r.NombreCliente,
                    r.TipoArchivo,
                    r.Resultado,
                    r.UsoIa,
                    r.ProveedorIa,
                    r.DuracionMs,
                    r.TokensEntrada,
                    r.TokensSalida,
                    r.CostoIaUsd,
                })
                .ToListAsync(cancellationToken);

            var llamadas = await enRango
                .SelectMany(r => r.LlamadasIa)
                .Select(l => new { l.Proveedor, l.Modelo, l.Exitosa, l.TokensEntrada, l.TokensSalida, l.CostoUsd })
                .ToListAsync(cancellationToken);

            var filas = registros.Select(r => new { Registro = r, Local = TimeZoneInfo.ConvertTimeFromUtc(r.FechaUtc, _zona) }).ToList();
            int dias = hasta.DayNumber - desde.DayNumber + 1;
            var duraciones = registros.Select(r => r.DuracionMs).OrderBy(d => d).ToList();

            var totales = new ResumenUso(
                Solicitudes: registros.Count,
                Procesadas: registros.Count(r => r.Resultado == RegistroUsoContexto.ResultadoOk),
                ConError: registros.Count(r => r.Resultado != RegistroUsoContexto.ResultadoOk),
                Pdf: registros.Count(r => r.TipoArchivo == TiposArchivo.Pdf),
                Imagenes: registros.Count(r => r.TipoArchivo == TiposArchivo.Imagen),
                DelegadasAIa: registros.Count(r => r.UsoIa),
                ResueltasPorIa: registros.Count(r => r.ProveedorIa != null),
                TokensEntrada: registros.Sum(r => r.TokensEntrada),
                TokensSalida: registros.Sum(r => r.TokensSalida),
                CostoIaUsd: registros.Sum(r => r.CostoIaUsd),
                ClientesActivos: registros.Select(r => r.IdUsuario).Distinct().Count(),
                PromedioSolicitudesPorDia: Math.Round((double)registros.Count / dias, 2),
                DuracionPromedioMs: duraciones.Count == 0 ? 0 : (int)duraciones.Average(),
                DuracionP95Ms: duraciones.Count == 0 ? 0 : duraciones[(int)Math.Ceiling(duraciones.Count * 0.95) - 1]);

            var porCliente = filas
                .GroupBy(f => f.Registro.IdUsuario)
                .Select(g =>
                {
                    var rs = g.Select(f => f.Registro).ToList();
                    int diasActivos = g.Select(f => f.Local.Date).Distinct().Count();
                    return new UsoPorCliente(
                        IdUsuario: g.Key,
                        Nombre: rs.OrderByDescending(r => r.FechaUtc).First().NombreCliente,
                        Solicitudes: rs.Count,
                        Procesadas: rs.Count(r => r.Resultado == RegistroUsoContexto.ResultadoOk),
                        Pdf: rs.Count(r => r.TipoArchivo == TiposArchivo.Pdf),
                        Imagenes: rs.Count(r => r.TipoArchivo == TiposArchivo.Imagen),
                        DelegadasAIa: rs.Count(r => r.UsoIa),
                        TokensEntrada: rs.Sum(r => r.TokensEntrada),
                        TokensSalida: rs.Sum(r => r.TokensSalida),
                        CostoIaUsd: rs.Sum(r => r.CostoIaUsd),
                        DiasActivos: diasActivos,
                        PromedioPorDiaActivo: Math.Round((double)rs.Count / diasActivos, 2),
                        PrimerUso: g.Min(f => f.Local),
                        UltimoUso: g.Max(f => f.Local));
                })
                .OrderByDescending(c => c.Solicitudes)
                .ToList();

            var porProveedor = llamadas
                .GroupBy(l => (l.Proveedor, l.Modelo))
                .Select(g => new UsoPorProveedorIa(
                    g.Key.Proveedor,
                    g.Key.Modelo,
                    Llamadas: g.Count(),
                    Exitosas: g.Count(l => l.Exitosa),
                    TokensEntrada: g.Sum(l => (long)l.TokensEntrada),
                    TokensSalida: g.Sum(l => (long)l.TokensSalida),
                    CostoUsd: g.Sum(l => l.CostoUsd)))
                .OrderByDescending(p => p.CostoUsd)
                .ToList();

            // Todos los días del rango, incluidos los que no tuvieron uso, para poder graficarlo tal cual.
            var porFecha = filas.ToLookup(f => DateOnly.FromDateTime(f.Local));
            var porDia = Enumerable.Range(0, dias)
                .Select(i => desde.AddDays(i))
                .Select(d => new UsoPorDia(
                    d,
                    porFecha[d].Count(),
                    porFecha[d].Count(f => f.Registro.UsoIa),
                    porFecha[d].Sum(f => f.Registro.CostoIaUsd)))
                .ToList();

            var porHoraLookup = filas.ToLookup(f => f.Local.Hour);
            var porHora = Enumerable.Range(0, 24).Select(h => new UsoPorHora(h, porHoraLookup[h].Count())).ToList();

            var porResultado = registros
                .GroupBy(r => r.Resultado)
                .Select(g => new UsoPorResultado(g.Key, g.Count()))
                .OrderByDescending(r => r.Cantidad)
                .ToList();

            return new EstadisticasUso(desde, hasta, _zona.Id, totales, porCliente, porProveedor, porDia, porHora, porResultado);
        }
    }
}
