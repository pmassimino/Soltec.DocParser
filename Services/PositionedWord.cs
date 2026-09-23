namespace Soltec.DocParser.Services
{
    // Una palabra con su posición en la página, independiente de si vino de PdfPig (texto
    // embebido del PDF) o de OCR (imagen). Todos los parsers trabajan sobre esto -no sobre el
    // tipo de PdfPig directamente- para poder alimentarlos indistintamente desde cualquiera de
    // las dos fuentes sin duplicar la lógica de reconstrucción de columnas/líneas.
    public class WordBox
    {
        public double Left { get; set; }
        public double Top { get; set; }
        public double Right { get; set; }
        public double Bottom { get; set; }
    }

    public class PositionedWord
    {
        public string Text { get; set; } = "";
        public WordBox BoundingBox { get; set; } = new();
    }
}
