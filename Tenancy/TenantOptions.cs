namespace Soltec.DocParser.Tenancy
{
    // Un cliente del servicio (p.ej. una instalación de SAE de un cliente de Soltec).
    // v1: la lista vive en appsettings; si esto crece, se puede mover a una tabla propia
    // sin cambiar el resto del código (todo lo demás depende solo de "Tenant", no de config).
    public class TenantOptions
    {
        public string ApiKey { get; set; } = "";
        public string Nombre { get; set; } = "";
    }
}
