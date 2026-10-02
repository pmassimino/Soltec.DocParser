namespace Soltec.DocParser.Uso
{
    // Sección "Uso" de appsettings.
    public sealed class UsoOptions
    {
        // Clave para GET /api/admin/uso (header ApiKey). Vacía = endpoint deshabilitado.
        public string ApiKey { get; set; } = "";

        // Zona horaria en la que se agrupan las estadísticas por día y por hora.
        public string ZonaHoraria { get; set; } = "America/Argentina/Buenos_Aires";

        // Precio en USD por millón de tokens, por modelo. La clave puede ser el nombre exacto o un
        // prefijo (p.ej. "claude-sonnet-5" cubre también "claude-sonnet-5-20260101").
        public Dictionary<string, PrecioModeloIa> PreciosIa { get; set; } = new();
    }

    public sealed class PrecioModeloIa
    {
        public decimal EntradaPorMillon { get; set; }
        // Tokens de entrada leídos de caché; si no se configura, se cobran como entrada normal.
        public decimal? EntradaCachePorMillon { get; set; }
        public decimal SalidaPorMillon { get; set; }
    }
}
