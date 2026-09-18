namespace Soltec.DocParser.Models
{
    public class DetalleFacturaCompra
    {
        public string Codigo { get; set; } = "";
        public string Concepto { get; set; } = "";
        public decimal Cantidad { get; set; }
        public string UnidadMedida { get; set; } = "";
        public decimal PrecioUnitario { get; set; }
        public decimal PorcentajeBonificacion { get; set; }
        public decimal ImporteBonificacion { get; set; }
        public decimal Subtotal { get; set; }
        public decimal AlicuotaIva { get; set; }
        public decimal SubtotalConIva { get; set; }
    }

    public class Factura
    {
        public string TipoComprobante { get; set; } = "";
        public string CodigoComprobante { get; set; } = "";
        public string Letra { get; set; } = "";
        public string PuntoVenta { get; set; } = "";
        public string Numero { get; set; } = "";
        public DateTime? FechaEmision { get; set; }
        public DateTime? FechaVencimientoPago { get; set; }
        public DateTime? PeriodoFacturadoDesde { get; set; }
        public DateTime? PeriodoFacturadoHasta { get; set; }
        public string CondicionVenta { get; set; } = "";

        public string ProveedorCuit { get; set; } = "";
        public string ProveedorRazonSocial { get; set; } = "";
        public string ProveedorDomicilio { get; set; } = "";
        public string ProveedorCondicionIva { get; set; } = "";
        public string ProveedorIngresosBrutos { get; set; } = "";

        public string ReceptorCuit { get; set; } = "";
        public string ReceptorRazonSocial { get; set; } = "";

        // true si el CUIT receptor coincide con el CUIT propio del tenant (=> es una compra a registrar)
        public bool EsCompra { get; set; }

        public decimal Subtotal { get; set; }
        public decimal ImporteNetoGravado { get; set; }
        public decimal Iva27 { get; set; }
        public decimal Iva21 { get; set; }
        public decimal Iva105 { get; set; }
        public decimal Iva5 { get; set; }
        public decimal Iva25 { get; set; }
        public decimal Iva0 { get; set; }
        public decimal ImporteOtrosTributos { get; set; }
        public decimal ImporteTotal { get; set; }

        public string Cae { get; set; } = "";
        public DateTime? FechaVtoCae { get; set; }

        public List<DetalleFacturaCompra> Detalle { get; set; } = new();

        // Diagnóstico: por qué el parseo puede no ser confiable. Nunca se debe registrar
        // en contabilidad un comprobante con advertencias sin revisión humana.
        public List<string> Advertencias { get; set; } = new();

        // true si el texto vino de OCR (imagen escaneada) en vez del texto embebido del PDF;
        // en ese caso la confiabilidad es menor y siempre debería revisarse a mano.
        public bool ObtenidoPorOcr { get; set; }
    }
}
