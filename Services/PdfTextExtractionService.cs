using System.Text.RegularExpressions;
using iTextSharp.text.pdf;
using iTextSharp.text.pdf.parser;

namespace Soltec.FacturaParser.Services
{
    public class PdfPageText
    {
        public List<string> Paginas { get; set; } = new();

        // Páginas únicas, sin las repeticiones de "ORIGINAL/DUPLICADO/TRIPLICADO" que
        // llevan el mismo contenido (copias físicas del mismo comprobante).
        public List<string> PaginasUnicas { get; set; } = new();

        public bool TieneTexto => PaginasUnicas.Any(p => !string.IsNullOrWhiteSpace(p));
    }

    public static class PdfTextExtractionService
    {
        public static PdfPageText ExtractText(byte[] pdfBytes)
        {
            var result = new PdfPageText();
            using var reader = new PdfReader(pdfBytes);
            for (int p = 1; p <= reader.NumberOfPages; p++)
            {
                string text = PdfTextExtractor.GetTextFromPage(reader, p, new LocationTextExtractionStrategy());
                result.Paginas.Add(text);
            }

            var normalizadas = result.Paginas
                .Select(t => Regex.Replace(t.Trim(), @"^\s*(ORIGINAL|DUPLICADO|TRIPLICADO|CUADRUPLICADO)\s*", "", RegexOptions.IgnoreCase))
                .ToList();
            result.PaginasUnicas = normalizadas.Distinct().ToList();
            return result;
        }
    }
}
