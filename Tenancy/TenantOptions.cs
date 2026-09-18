namespace Soltec.DocParser.Tenancy
{
    // Un cliente del servicio (p.ej. una instalación de SAE de un cliente de Soltec).
    // v1: la lista vive en appsettings; si esto crece, se puede mover a una tabla propia
    // sin cambiar el resto del código (todo lo demás depende solo de "Tenant", no de config).
    public class TenantOptions
    {
        public string ApiKey { get; set; } = "";
        public string Nombre { get; set; } = "";

        // CUIT propio del cliente (sin guiones). Se usa para decidir si un comprobante
        // es una compra (el cliente es el receptor) o no.
        public string Cuit { get; set; } = "";
    }
}
