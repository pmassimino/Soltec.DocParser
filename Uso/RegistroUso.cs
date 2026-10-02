namespace Soltec.DocParser.Uso
{
    // Una fila por cada comprobante que un cliente mandó a procesar (/pdf o /imagen), haya salido
    // bien o no. Los totales de IA se guardan acá ya sumados para no tener que recorrer las
    // llamadas en cada reporte; el detalle por llamada queda en LlamadasIa.
    public sealed class RegistroUso
    {
        public long Id { get; set; }
        public DateTime FechaUtc { get; set; }
        public int IdUsuario { get; set; }
        public string NombreCliente { get; set; } = "";
        // TiposArchivo.Pdf o TiposArchivo.Imagen.
        public string TipoArchivo { get; set; } = "";
        public string? NombreArchivo { get; set; }
        public long TamanoBytes { get; set; }
        // Páginas con contenido del PDF (null para imágenes o si no se llegó a abrir).
        public int? Paginas { get; set; }
        // "OK", el código de error de negocio (p.ej. SIN_TEXTO_EXTRAIBLE), "HTTP_400" o "EXCEPCION".
        public string Resultado { get; set; } = "";
        // true = se delegó a la IA (aunque la IA haya fallado).
        public bool UsoIa { get; set; }
        // Proveedor que resolvió el comprobante; null si no se usó IA o ninguno pudo.
        public string? ProveedorIa { get; set; }
        public int DuracionMs { get; set; }
        public long TokensEntrada { get; set; }
        public long TokensSalida { get; set; }
        public decimal CostoIaUsd { get; set; }
        public List<RegistroUsoIa> LlamadasIa { get; set; } = new();
    }

    public sealed class RegistroUsoIa
    {
        public long Id { get; set; }
        public long RegistroUsoId { get; set; }
        public string Proveedor { get; set; } = "";
        public string Modelo { get; set; } = "";
        public int TokensEntrada { get; set; }
        public int TokensEntradaCache { get; set; }
        public int TokensSalida { get; set; }
        // Calculado al momento de la llamada con los precios configurados entonces, para que un
        // cambio de precio posterior no reescriba el costo histórico.
        public decimal CostoUsd { get; set; }
        public bool Exitosa { get; set; }
    }

    public static class TiposArchivo
    {
        public const string Pdf = "PDF";
        public const string Imagen = "IMAGEN";
    }
}
