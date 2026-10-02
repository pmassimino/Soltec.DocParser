namespace Soltec.DocParser.Tenancy
{
    // Conexión con Soltec.Suscripcion, que es quien emite los tokens (POST /api/login) y sabe qué
    // cuentas tienen la suscripción vigente. Jwt debe coincidir con la sección "Jwt" de Suscripcion.
    public class SuscripcionOptions
    {
        public string UrlService { get; set; } = "";
        // Id del plan (tabla Plan de Suscripcion) que habilita DocParser. 0 = alcanza con
        // cualquier suscripción vigente de la cuenta.
        public int IdPlanRequerido { get; set; } = 3;
        public int CacheMinutos { get; set; } = 10;
        public JwtOptions Jwt { get; set; } = new();
    }

    public class JwtOptions
    {
        public string Key { get; set; } = "";
        public string Issuer { get; set; } = "Soltec.Suscripcion";
        public string Audience { get; set; } = "Soltec.Suscripcion";
    }
}
