namespace Soltec.DocParser.Models
{
    // Un importe identificado por código, para listas de longitud variable (alícuotas de IVA,
    // percepciones, impuestos) en vez de una propiedad fija por cada una -así un comprobante con
    // una alícuota que no se había visto todavía (o sin alguna de las habituales) no obliga a
    // tocar el modelo.
    public class ImporteConId
    {
        public string Id { get; set; } = "";
        public decimal Importe { get; set; }
    }

    public class DetalleFactura
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

        // CTG / número de carta de porte del ítem (siempre 11 dígitos), cuando la descripción lo
        // trae -típico en facturas de acopio/flete de granos, para poder cruzar contra la carta
        // de porte y controlar que tarifa, peso y CTG coincidan con lo facturado-. Vacío si el
        // ítem no menciona uno.
        public string Ctg { get; set; } = "";
        public decimal? Peso { get; set; }
        public decimal? Tarifa { get; set; }
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

        // Una entrada por alícuota con importe, Id="IVA21"/"IVA105"/"IVA27"/"IVA5"/"IVA2_5"/"IVA0".
        // Si el comprobante no discrimina por alícuota (algunos formatos SAE solo muestran un
        // "Iva. General"), se usa "IVA21" igual -es la alícuota general/estándar en Argentina-.
        public List<ImporteConId> Ivas { get; set; } = new();

        // Percepciones/impuestos/otros importes del comprobante que no son IVA (Percepción IB,
        // Percepción IVA, Impuesto Interno, "No Gravado", etc.), cada uno con su Id tal como lo
        // llama el comprobante.
        public List<ImporteConId> OtrosTributos { get; set; } = new();

        // Sumas de las listas, para no tener que recorrerlas cada vez que solo hace falta el total.
        public decimal ImporteIva => Ivas.Sum(i => i.Importe);
        public decimal TotalOtrosTributos => OtrosTributos.Sum(o => o.Importe);
        public decimal ImporteTotal { get; set; }

        public string Cae { get; set; } = "";
        public DateTime? FechaVtoCae { get; set; }

        public List<DetalleFactura> Detalle { get; set; } = new();

        // Diagnóstico: por qué el parseo puede no ser confiable. Nunca se debe registrar
        // en contabilidad un comprobante con advertencias sin revisión humana.
        public List<string> Advertencias { get; set; } = new();

        // true si el texto vino de OCR (imagen escaneada) en vez del texto embebido del PDF;
        // en ese caso la confiabilidad es menor y siempre debería revisarse a mano.
        public bool ObtenidoPorOcr { get; set; }
    }
}
