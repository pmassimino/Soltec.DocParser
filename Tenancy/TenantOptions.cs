namespace Soltec.DocParser.Tenancy
{
    // Un cliente del servicio (p.ej. una instalación de SAE de un cliente de Soltec), resuelto a
    // partir del token emitido por Soltec.Suscripcion. Todo lo demás depende solo de "Tenant", no
    // de cómo se autenticó.
    public class TenantOptions
    {
        public int IdUsuario { get; set; }
        public string Nombre { get; set; } = "";
    }
}
