using Tesseract;

namespace Soltec.DocParser.Services
{
    // Extrae texto + posición de cada palabra de una imagen (foto/escaneo de una factura, sin
    // texto embebido como en un PDF) usando OCR, y arma un ExtractedPage con la misma forma que
    // PdfPigExtraction -por eso todos los parsers existentes funcionan igual sin cambios: no les
    // importa si las palabras vinieron de PdfPig o de acá, ambas cosas ya son PositionedWord.
    //
    // La calidad depende directamente de la imagen de entrada (resolución, foco, luz); una foto
    // de celular de baja calidad puede dar palabras mal reconocidas incluso con esto bien armado
    // -no hay manera de que el parsing "arregle" texto que el OCR ya leyó mal-. Por eso el
    // resultado siempre marca ObtenidoPorOcr = true en el modelo, para que se revise a mano.
    public static class OcrExtraction
    {
        static string? _tessdataDir;

        public static void Configurar(string tessdataDir) => _tessdataDir = tessdataDir;

        public static ExtractedPage ExtraerDeImagen(byte[] imageBytes)
        {
            if (_tessdataDir == null)
                throw new InvalidOperationException("OcrExtraction.Configurar(tessdataDir) no fue llamado al iniciar la app.");

            using var engine = new TesseractEngine(_tessdataDir, "spa", EngineMode.Default);
            using var img = Pix.LoadFromMemory(imageBytes);
            using var page = engine.Process(img);

            var words = new List<PositionedWord>();
            using (var iter = page.GetIterator())
            {
                iter.Begin();
                do
                {
                    if (!iter.TryGetBoundingBox(PageIteratorLevel.Word, out var rect)) continue;
                    string texto = iter.GetText(PageIteratorLevel.Word)?.Trim() ?? "";
                    if (texto.Length == 0) continue;

                    // Tesseract da coordenadas de imagen (Y crece hacia abajo, origen arriba a la
                    // izquierda); PdfPig -y todo lo que se construyó sobre eso- espera coordenadas
                    // tipo PDF (Y crece hacia arriba, origen abajo a la izquierda). Se invierte el
                    // eje Y con el alto de la imagen para que ambas fuentes queden en el mismo
                    // sistema y GroupIntoLines/ExtractItemRows no tengan que saber de dónde vino cada palabra.
                    words.Add(new PositionedWord
                    {
                        Text = texto,
                        BoundingBox = new WordBox
                        {
                            Left = rect.X1,
                            Right = rect.X2,
                            Top = img.Height - rect.Y1,
                            Bottom = img.Height - rect.Y2,
                        }
                    });
                } while (iter.Next(PageIteratorLevel.Word));
            }

            var lines = PdfPigExtraction.GroupIntoLines(words);
            var lineText = string.Join("\n", lines.Select(l => string.Join(" ", l.OrderBy(w => w.BoundingBox.Left).Select(w => w.Text))));

            return new ExtractedPage { LineText = lineText, Words = words };
        }
    }
}
