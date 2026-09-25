using PDFtoImage;
using SkiaSharp;

namespace Soltec.DocParser.Services
{
    // Convierte una página del comprobante (PDF o imagen) en una imagen lista para mostrar en la
    // pantalla de revisión de un cliente que no puede embeber un visor de PDF (p.ej. un form de
    // VFP con un control Image). No interviene en la extracción: es solo para que la persona que
    // revisa vea el original al lado de los datos extraídos.
    public static class PaginaRender
    {
        public const int DpiMinimo = 50;
        public const int DpiMaximo = 300;

        public static int ContarPaginasPdf(byte[] pdfBytes) => Conversion.GetPageCount(pdfBytes);

        // paginaIndex es base 0. Devuelve la imagen ya codificada en el formato pedido.
        public static byte[] RenderizarPdf(byte[] pdfBytes, int paginaIndex, int dpi, SKEncodedImageFormat formato)
        {
            using SKBitmap bitmap = Conversion.ToImage(pdfBytes, page: paginaIndex, options: new RenderOptions(Dpi: dpi, WithAnnotations: true));
            return Codificar(bitmap, formato);
        }

        // Una foto de celular suele venir con la orientación en los metadatos EXIF en vez de
        // rotada de verdad; se aplica acá para que la factura no aparezca de costado. anchoMaximo
        // evita mandar una foto de 12 MP a una pantalla que la va a mostrar a 1000 px.
        public static byte[]? RenderizarImagen(byte[] imageBytes, int anchoMaximo, SKEncodedImageFormat formato)
        {
            using var codec = SKCodec.Create(new MemoryStream(imageBytes));
            if (codec == null) return null;

            using var original = SKBitmap.Decode(codec);
            if (original == null) return null;

            using var orientada = AplicarOrientacion(original, codec.EncodedOrigin);
            var bitmap = orientada;
            SKBitmap? reducida = null;
            if (bitmap.Width > anchoMaximo)
            {
                int alto = (int)Math.Round(bitmap.Height * (anchoMaximo / (double)bitmap.Width));
                reducida = bitmap.Resize(new SKSizeI(anchoMaximo, alto), SKFilterQuality.High);
                if (reducida != null) bitmap = reducida;
            }

            try
            {
                return Codificar(bitmap, formato);
            }
            finally
            {
                reducida?.Dispose();
            }
        }

        static SKBitmap AplicarOrientacion(SKBitmap bmp, SKEncodedOrigin origen)
        {
            int grados = origen switch
            {
                SKEncodedOrigin.BottomRight => 180,
                SKEncodedOrigin.RightTop => 90,
                SKEncodedOrigin.LeftBottom => 270,
                _ => 0,
            };
            if (grados == 0) return bmp.Copy();

            bool intercambiar = grados != 180;
            var rotada = new SKBitmap(intercambiar ? bmp.Height : bmp.Width, intercambiar ? bmp.Width : bmp.Height);
            using var canvas = new SKCanvas(rotada);
            canvas.Translate(rotada.Width / 2f, rotada.Height / 2f);
            canvas.RotateDegrees(grados);
            canvas.Translate(-bmp.Width / 2f, -bmp.Height / 2f);
            canvas.DrawBitmap(bmp, 0, 0);
            return rotada;
        }

        static byte[] Codificar(SKBitmap bitmap, SKEncodedImageFormat formato)
        {
            // JPEG no tiene transparencia (el fondo transparente de algunos PDF saldría negro) y el
            // encoder de Skia no acepta cualquier combinación de color/alpha que devuelve el render
            // del PDF: se aplana siempre sobre un bitmap estándar con fondo blanco.
            if (formato == SKEncodedImageFormat.Jpeg)
            {
                using var fondoBlanco = new SKBitmap(new SKImageInfo(bitmap.Width, bitmap.Height, SKImageInfo.PlatformColorType, SKAlphaType.Premul));
                using (var canvas = new SKCanvas(fondoBlanco))
                {
                    canvas.Clear(SKColors.White);
                    canvas.DrawBitmap(bitmap, 0, 0);
                }
                return Encode(fondoBlanco, formato);
            }
            return Encode(bitmap, formato);
        }

        static byte[] Encode(SKBitmap bitmap, SKEncodedImageFormat formato)
        {
            using var image = SKImage.FromBitmap(bitmap);
            using var data = image.Encode(formato, formato == SKEncodedImageFormat.Jpeg ? 85 : 100)
                ?? throw new InvalidOperationException($"No se pudo codificar la página como {formato}.");
            return data.ToArray();
        }
    }
}
