using System.Globalization;
using System.Text.RegularExpressions;
using Soltec.DocParser.Models;
using UglyToad.PdfPig.Content;

namespace Soltec.DocParser.Services
{
    // Parser para el formato de factura que genera el sistema SAE (el mismo motor de reportes
    // que usa este propio proyecto, Soltec.Sae.Api/FacturaTemplate.cs). No es un formato de UN
    // proveedor puntual: lo genera cualquier instalación de SAE, cambiando solo el CUIT/logo del
    // emisor -por eso la detección y la extracción del emisor son genéricas, nunca un CUIT fijo.
    //
    // Se vieron 2 variantes de layout para los totales (probablemente por versión del reporte):
    //  - "en línea": cada etiqueta y su valor están en el mismo renglón visual (Union Agrícola).
    //  - "en tabla": todas las etiquetas van en un renglón y todos los valores en el siguiente,
    //    alineados por columna (Monte Maíz). Se intenta la primera y, si no da resultado, la segunda.
    //
    // También hay una variante más vieja de encabezado ("Señor/es" / "CUIT N°" / "Tipo 1" en vez
    // de "Cliente" / "C.U.I.T" / "Cod. NN") que permite varios ítems con IVA distinto por línea;
    // se reconoce el encabezado pero el detalle de ítems todavía no se parsea para esa variante.
    public static class SaeFacturaParser
    {
        static readonly Dictionary<int, string> CodigoALetra = new()
        {
            [1] = "A", [6] = "B", [11] = "C", [51] = "M",
        };

        // Los importes pueden venir en formato argentino ("48.652,200": "." miles, "," decimal)
        // o "internacional" ("1,700.826": "," miles, "." decimal) según la instalación de SAE
        // que generó el PDF. El separador decimal es el que aparece más a la derecha del número.
        const string NUM = @"\d[\d.,]*\d";

        static decimal ParseNumeroAmbiguo(string s)
        {
            s = s.Trim();
            int lastComma = s.LastIndexOf(',');
            int lastDot = s.LastIndexOf('.');
            char decimalSep = lastComma > lastDot ? ',' : '.';
            char milesSep = decimalSep == ',' ? '.' : ',';
            string limpio = s.Replace(milesSep.ToString(), "").Replace(decimalSep, '.');
            return decimal.Parse(limpio, CultureInfo.InvariantCulture);
        }

        static decimal ParseNumeroOZero(string? s)
        {
            if (string.IsNullOrWhiteSpace(s)) return 0m;
            var m = Regex.Match(s, NUM);
            return m.Success ? ParseNumeroAmbiguo(m.Value) : 0m;
        }

        static DateTime? ParseArDate(string? s)
        {
            if (string.IsNullOrWhiteSpace(s)) return null;
            return DateTime.TryParseExact(s.Trim(), "dd/MM/yyyy", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? d : null;
        }

        // Firma estructural del formato (etiquetas propias de SAE), sin depender de qué empresa
        // lo emitió: cualquier CUIT que use este mismo motor de reportes debe reconocerse igual.
        public static bool EsFormatoSae(string lineText)
        {
            string text = Regex.Replace(lineText, @"\s+", " ");
            bool tieneCliente = Regex.IsMatch(text, @"\bCliente\b") && Regex.IsMatch(text, @"C\.U\.I\.T");
            bool tieneSenores = Regex.IsMatch(text, @"Se[ñn]or/es") && Regex.IsMatch(text, @"CUIT\s*N[°º]");
            bool tieneTabla = Regex.IsMatch(text, @"C[oó]digo\s+Cantidad\s+Detalle") || Regex.IsMatch(text, @"CODIGO\s+CANTIDAD\s+DESCRIPCION");
            bool tieneCae = Regex.IsMatch(text, @"\bCAE\b");
            return (tieneCliente || tieneSenores) && tieneTabla && tieneCae;
        }

        // Parser genérico "para todo lo que no sea ARCA": conoce bien las etiquetas del formato
        // SAE, pero no rechaza un PDF solo porque no las encuentre -intenta extraer lo que pueda
        // reconocer (Numero, CAE, fechas, CUIT, etc. usan patrones bastante genéricos) y deja
        // advertencias explícitas por cada campo que no encuentra, en vez de negarse a intentarlo.
        public static Factura Parse(string lineText, List<Word> words, string empresaCuit)
        {
            var result = new Factura();
            string text = Regex.Replace(lineText, @"\s+", " ").Trim();

            if (!EsFormatoSae(lineText))
                result.Advertencias.Add("El documento no coincide con la estructura habitual de este parser (SAE); los campos extraídos pueden estar incompletos o vacíos.");

            bool esVariantePlana = Regex.IsMatch(text, @"\bCliente\b") && Regex.IsMatch(text, @"C\.U\.I\.T");
            bool esVarianteVieja = !esVariantePlana && Regex.IsMatch(text, @"Se[ñn]or/es");

            result.TipoComprobante = "FACTURA";

            var mCodigo = Regex.Match(text, @"Cod\.\s*(\d+)|Tipo\s*(\d+)");
            if (mCodigo.Success)
            {
                var codigoStr = mCodigo.Groups[1].Success ? mCodigo.Groups[1].Value : mCodigo.Groups[2].Value;
                result.CodigoComprobante = codigoStr;
                if (int.TryParse(codigoStr, out var codigoInt) && CodigoALetra.TryGetValue(codigoInt, out var letra))
                    result.Letra = letra;
            }
            if (string.IsNullOrEmpty(result.Letra))
            {
                var mLetra = Regex.Match(text, @"\bFACTURA\b.*?\b(A|B|C|M)\b");
                if (mLetra.Success) result.Letra = mLetra.Groups[1].Value;
            }

            var mNumero = Regex.Match(text, @"(?:Numero|N[ºo]\.?)\s*(\d{4})[\s-]+(\d{5,8})");
            if (mNumero.Success)
            {
                result.PuntoVenta = mNumero.Groups[1].Value;
                result.Numero = mNumero.Groups[2].Value;
            }
            else
            {
                result.Advertencias.Add("No se pudo extraer Punto de Venta / Número de comprobante.");
            }

            var mFecha = Regex.Match(text, @"\bFecha:?\s*(\d{2}/\d{2}/\d{4})");
            result.FechaEmision = ParseArDate(mFecha.Success ? mFecha.Groups[1].Value : null);

            var mFechaVto = Regex.Match(text, @"Fecha Vto\.?\s*(\d{2}/\d{2}/\d{4})");
            result.FechaVencimientoPago = ParseArDate(mFechaVto.Success ? mFechaVto.Groups[1].Value : null);

            var mCondVenta = Regex.Match(text, @"Cond\.\s*Vent[ae]\.?\s*(.+?)\s*(?=Fecha Vto\.|C[oó]digo|CODIGO|$)");
            if (mCondVenta.Success) result.CondicionVenta = mCondVenta.Groups[1].Value.Trim();

            // ---- Emisor: el CUIT sí está en el texto ("CUIT : ..."), pero la razón social suele
            // ser parte del logo (imagen), no texto extraíble -se deja vacía en vez de inventarla-.
            var mEmisorCuit = Regex.Match(text, @"(?<!\.)(?<!N[°º]\s*)\bCUIT\s*:?\s*([\d-]{11,13})(?!\d)");
            if (mEmisorCuit.Success) result.ProveedorCuit = Regex.Replace(mEmisorCuit.Groups[1].Value, @"\D", "");
            else result.Advertencias.Add("No se pudo extraer el CUIT del emisor.");

            var mEmisorIIBB = Regex.Match(text, @"Ing\.\s*Brutos:\s*([\d-]+)");
            if (mEmisorIIBB.Success) result.ProveedorIngresosBrutos = mEmisorIIBB.Groups[1].Value;

            // ---- Receptor (cliente) ----
            var mReceptorNombre = Regex.Match(text, @"(?:Se[ñn]or/es|Cliente)\s+(.+?)\s*(?=Cuenta|Domicilio|$)");
            if (mReceptorNombre.Success) result.ReceptorRazonSocial = mReceptorNombre.Groups[1].Value.Trim();
            else result.Advertencias.Add("No se pudo extraer el nombre del cliente (receptor).");

            var mReceptorCuit = Regex.Match(text, @"CUIT\s*N[°º]\s*(\d{11})|C\.U\.I\.T\s*(\d{11})");
            if (mReceptorCuit.Success)
                result.ReceptorCuit = mReceptorCuit.Groups[1].Success ? mReceptorCuit.Groups[1].Value : mReceptorCuit.Groups[2].Value;
            else
                result.Advertencias.Add("No se pudo extraer el CUIT del cliente (receptor).");

            string cuitPropio = Regex.Replace(empresaCuit ?? "", @"\D", "");
            if (!string.IsNullOrEmpty(result.ReceptorCuit) && result.ReceptorCuit == cuitPropio)
            {
                result.EsCompra = true;
            }
            else if (!string.IsNullOrEmpty(result.ProveedorCuit) && result.ProveedorCuit == cuitPropio)
            {
                result.EsCompra = false;
                result.Advertencias.Add("El CUIT emisor coincide con el de la propia empresa: este comprobante es una VENTA, no una compra.");
            }
            else
            {
                result.Advertencias.Add("Ninguno de los CUIT detectados coincide con el CUIT configurado para este cliente. Verificar manualmente si corresponde registrar como compra.");
            }

            var mCae = Regex.Match(text, @"\bCAE\s*:?\s*(\d+)");
            if (mCae.Success) result.Cae = mCae.Groups[1].Value;
            else result.Advertencias.Add("No se pudo extraer el CAE del comprobante.");

            var mVtoCae = Regex.Match(text, @"Fecha Venc\.\s*Cae:?\s*(\d{2}/\d{2}/\d{4})");
            result.FechaVtoCae = ParseArDate(mVtoCae.Success ? mVtoCae.Groups[1].Value : null);

            if (esVariantePlana)
                ExtraerItemsYTotales(text, words, result);
            else if (esVarianteVieja)
                result.Advertencias.Add("Comprobante de la variante antigua de este formato (encabezado 'Señor/es'): el detalle de ítems todavía no se parsea automáticamente. Cargar manualmente.");
            else
                result.Advertencias.Add("No se pudo determinar la variante del formato SAE para extraer ítems y totales.");

            return result;
        }

        static void ExtraerItemsYTotales(string text, List<Word> words, Factura result)
        {
            var lines = PdfPigExtraction.GroupIntoLines(words, 3.0);

            var headerLine = lines.FirstOrDefault(l => l.Any(w => w.Text.Equals("Código", StringComparison.OrdinalIgnoreCase))
                                                     && l.Any(w => w.Text.Equals("Detalle", StringComparison.OrdinalIgnoreCase)));
            if (headerLine == null)
            {
                result.Advertencias.Add("No se pudo detectar ningún ítem de detalle en la tabla del comprobante.");
                return;
            }

            var totalesLabelLine = lines.FirstOrDefault(l => l.Any(w => w.Text.Equals("Sub.", StringComparison.OrdinalIgnoreCase))
                                                            && l.Any(w => w.Text.Equals("Total", StringComparison.OrdinalIgnoreCase)));
            double tablaTop = headerLine[0].BoundingBox.Top;
            double tablaBottom = totalesLabelLine != null ? totalesLabelLine[0].BoundingBox.Top : double.MinValue;

            var enTabla = words.Where(w => w.BoundingBox.Top < tablaTop && w.BoundingBox.Top > tablaBottom).ToList();

            var headerAnchors = new List<(string label, double left)>();
            void AddAnchor(string label, Func<Word, bool> match)
            {
                var w = headerLine.FirstOrDefault(match);
                if (w != null) headerAnchors.Add((label, w.BoundingBox.Left));
            }
            AddAnchor("Codigo", w => w.Text.Equals("Código", StringComparison.OrdinalIgnoreCase));
            AddAnchor("Cantidad", w => w.Text.Equals("Cantidad", StringComparison.OrdinalIgnoreCase));
            AddAnchor("Detalle", w => w.Text.Equals("Detalle", StringComparison.OrdinalIgnoreCase));
            AddAnchor("Remito", w => w.Text.Equals("Remito", StringComparison.OrdinalIgnoreCase));
            AddAnchor("Precio", w => w.Text.Equals("Precio", StringComparison.OrdinalIgnoreCase));
            AddAnchor("Importe", w => w.Text.Equals("Importe", StringComparison.OrdinalIgnoreCase));
            headerAnchors = headerAnchors.OrderBy(a => a.left).ToList();

            if (headerAnchors.Count == 0)
            {
                result.Advertencias.Add("No se pudo detectar ningún ítem de detalle en la tabla del comprobante.");
            }
            else
            {
                // Las etiquetas de encabezado no siempre quedan alineadas al borde izquierdo real
                // de los valores de esa columna (p.ej. "Detalle" puede aparecer más centrada que
                // donde arranca el texto real), así que el límite entre columnas se define a
                // mitad de camino entre etiquetas consecutivas en vez de en el borde exacto.
                var columns = new List<TableColumn>();
                for (int i = 0; i < headerAnchors.Count; i++)
                {
                    double left = i == 0 ? double.MinValue : (headerAnchors[i - 1].left + headerAnchors[i].left) / 2.0;
                    double right = i + 1 < headerAnchors.Count ? (headerAnchors[i].left + headerAnchors[i + 1].left) / 2.0 : double.MaxValue;
                    columns.Add(new TableColumn { Name = headerAnchors[i].label, Left = left, Right = right });
                }
                string ColumnFor(double x) => columns.FirstOrDefault(c => x >= c.Left && x < c.Right)?.Name ?? "?";

                // Exige un número decimal completo en "Cantidad" (no solo texto que arranca con
                // un dígito), porque una descripción envuelta en 2 líneas puede dejar un número
                // suelto (p.ej. de un número de referencia) en esa franja de X.
                var itemLines = PdfPigExtraction.GroupIntoLines(enTabla, 3.0)
                    .Where(l => l.Any(w => ColumnFor(w.BoundingBox.Left) == "Cantidad" && Regex.IsMatch(w.Text, "^" + NUM + "$")))
                    .ToList();

                if (itemLines.Count == 0)
                    result.Advertencias.Add("No se pudo detectar ningún ítem de detalle en la tabla del comprobante.");

                foreach (var linea in itemLines)
                {
                    var porColumna = linea.GroupBy(w => ColumnFor(w.BoundingBox.Left))
                        .ToDictionary(g => g.Key, g => string.Join(" ", g.OrderBy(w => w.BoundingBox.Left).Select(w => w.Text)));

                    var detalle = new DetalleFacturaCompra
                    {
                        Codigo = porColumna.GetValueOrDefault("Codigo", "").Trim(),
                        Concepto = porColumna.GetValueOrDefault("Detalle", "").Trim(),
                        Cantidad = ParseNumeroOZero(porColumna.GetValueOrDefault("Cantidad")),
                        PrecioUnitario = ParseNumeroOZero(porColumna.GetValueOrDefault("Precio")),
                        Subtotal = ParseNumeroOZero(porColumna.GetValueOrDefault("Importe")),
                    };

                    // Salvaguarda: sin importe no es un ítem real -puede ser el resto de una
                    // descripción envuelta, o un número suelto del pie de página (p.ej. "T.C.:
                    // 1396.000", la cotización del dólar) que cayó en la franja de "Cantidad".
                    if (detalle.Subtotal == 0) continue;

                    result.Detalle.Add(detalle);
                }
            }

            ExtraerTotales(text, words, totalesLabelLine, result);
        }

        // Agrega (o suma, si ya hay una entrada con el mismo Id) un importe de IVA a la lista,
        // identificado por alícuota ("IVA21", "IVA105", etc., igual que el parser ARCA). "General"
        // es la alícuota estándar (21%) en Argentina. Una etiqueta que no sea ninguna alícuota
        // conocida de AFIP (p.ej. "Iva. 11", vista en comprobantes reales) igual se agrega, con
        // su propio Id derivado de la etiqueta, en vez de perderse mezclada en un total.
        static void AgregarIva(Factura result, string etiqueta, decimal importe)
        {
            if (importe == 0) return;
            string id = etiqueta.Trim() switch
            {
                "General" => "IVA21",
                "27" => "IVA27",
                "21" => "IVA21",
                "10.5" or "10,5" => "IVA105",
                "5" => "IVA5",
                "2.5" or "2,5" => "IVA25",
                "0" => "IVA0",
                var otra => "IVA_" + Regex.Replace(otra, @"[^\w]", ""),
            };
            var existente = result.Ivas.FirstOrDefault(i => i.Id == id);
            if (existente != null) existente.Importe += importe;
            else result.Ivas.Add(new ImporteConId { Id = id, Importe = importe });
        }

        static void AgregarPercepcion(Factura result, string id, decimal importe)
        {
            if (importe == 0) return;
            var existente = result.Percepciones.FirstOrDefault(p => p.Id == id);
            if (existente != null) existente.Importe += importe;
            else result.Percepciones.Add(new ImporteConId { Id = id, Importe = importe });
        }

        static void ExtraerTotales(string text, List<Word> words, List<Word>? totalesLabelLine, Factura result)
        {
            // Variante "en línea": cada etiqueta y su valor comparten renglón, así que quedan
            // adyacentes en el texto reconstruido (ver Union Agrícola).
            var mSubtotal = Regex.Match(text, @"Sub\.\s*Total\s+(" + NUM + ")");
            var mTotal = Regex.Match(text, @"(?<!Sub\.\s)Total\s+(" + NUM + ")");
            if (mSubtotal.Success && mTotal.Success)
            {
                result.Subtotal = ParseNumeroOZero(mSubtotal.Groups[1].Value);
                result.ImporteTotal = ParseNumeroOZero(mTotal.Groups[1].Value);
                result.ImporteNetoGravado = result.Subtotal;

                foreach (Match m in Regex.Matches(text, @"Iva\.\s*(General|\d+(?:[.,]\d+)?)\s+(" + NUM + ")"))
                    AgregarIva(result, m.Groups[1].Value, ParseNumeroOZero(m.Groups[2].Value));

                var mDesc = Regex.Match(text, @"\bDesc\.\s+(" + NUM + ")");
                if (mDesc.Success) AgregarPercepcion(result, "Desc", ParseNumeroOZero(mDesc.Groups[1].Value));
                var mPerc = Regex.Match(text, @"(?:Percepci[oó]n:|Perc\.)\s+(" + NUM + ")");
                if (mPerc.Success) AgregarPercepcion(result, "Perc", ParseNumeroOZero(mPerc.Groups[1].Value));
                var mImp = Regex.Match(text, @"\bImpuestos\s+(" + NUM + ")");
                if (mImp.Success) AgregarPercepcion(result, "Impuestos", ParseNumeroOZero(mImp.Groups[1].Value));
                var mNg = Regex.Match(text, @"\bN\.G\s+(" + NUM + ")");
                if (mNg.Success) AgregarPercepcion(result, "NoGravado", ParseNumeroOZero(mNg.Groups[1].Value));

                AvisarSiHayDiferenciaSinExplicar(result);
                return;
            }

            // Variante "en tabla": una línea con todas las etiquetas y, en la línea de arriba (más
            // Top), otra con todos los valores alineados por columna (ver Monte Maíz/Monte Buey).
            // Se reconstruyen las columnas agrupando las palabras de la línea de etiquetas según
            // una gramática conocida (las combinaciones posibles de este formato: "Sub. Total",
            // "Sub. Total 2", "Desc.", "Perc.", "N.G", "Impuestos", "Iva. <alícuota>", "Total"),
            // y se empareja cada columna con el valor que está en la misma posición de la fila
            // de valores -no con el primero/último nomás-, para no confundir Percepción/Impuestos
            // (que no son IVA) con las columnas que sí lo son.
            if (totalesLabelLine != null)
            {
                var valoresLine = PdfPigExtraction.GroupIntoLines(words, 3.0)
                    .FirstOrDefault(l => l[0].BoundingBox.Top < totalesLabelLine[0].BoundingBox.Top
                                      && l.Count(w => Regex.IsMatch(w.Text, @"^" + NUM + "$")) >= 2);
                if (valoresLine == null)
                {
                    result.Advertencias.Add("No se pudieron extraer los totales del comprobante.");
                    return;
                }

                var valores = valoresLine.Where(w => Regex.IsMatch(w.Text, @"^" + NUM + "$"))
                    .OrderBy(w => w.BoundingBox.Left).Select(w => w.Text).ToList();

                var etiquetasOrdenadas = totalesLabelLine.OrderBy(w => w.BoundingBox.Left).ToList();
                var columnas = new List<string>();
                for (int i = 0; i < etiquetasOrdenadas.Count; i++)
                {
                    string t = etiquetasOrdenadas[i].Text;
                    if (t.Equals("Sub.", StringComparison.OrdinalIgnoreCase))
                    {
                        bool esSegundo = i + 2 < etiquetasOrdenadas.Count && etiquetasOrdenadas[i + 2].Text == "2";
                        columnas.Add(esSegundo ? "SubTotal2" : "SubTotal1");
                        i += esSegundo ? 2 : 1;
                    }
                    else if (t.Equals("Iva.", StringComparison.OrdinalIgnoreCase) && i + 1 < etiquetasOrdenadas.Count)
                    {
                        columnas.Add("Iva:" + etiquetasOrdenadas[i + 1].Text);
                        i += 1;
                    }
                    else if (t.Equals("Total", StringComparison.OrdinalIgnoreCase))
                    {
                        columnas.Add("Total");
                    }
                    else
                    {
                        columnas.Add(t); // Desc. / Perc. / N.G / Impuestos, etc. - no son IVA
                    }
                }

                if (columnas.Count != valores.Count)
                {
                    // La gramática no calzó 1 a 1 con los valores (formato no previsto); se cae
                    // al criterio más simple -primer valor Subtotal, último Total- para no dejar
                    // esos dos campos vacíos, pero sin arriesgar a qué alícuota corresponde el resto.
                    result.Subtotal = ParseNumeroOZero(valores.First());
                    result.ImporteTotal = ParseNumeroOZero(valores.Last());
                    result.ImporteNetoGravado = result.Subtotal;
                    if (result.ImporteTotal != result.Subtotal)
                        result.Ivas.Add(new ImporteConId { Id = "IVA_SIN_DISCRIMINAR", Importe = result.ImporteTotal - result.Subtotal });
                    result.Advertencias.Add("No se pudo relacionar cada columna de totales con su valor (formato no previsto); el IVA no se discrimina por alícuota.");
                    return;
                }

                for (int i = 0; i < columnas.Count; i++)
                {
                    decimal valor = ParseNumeroOZero(valores[i]);
                    if (columnas[i] == "SubTotal1") result.Subtotal = valor;
                    else if (columnas[i] == "Total") result.ImporteTotal = valor;
                    else if (columnas[i].StartsWith("Iva:")) AgregarIva(result, columnas[i].Substring(4), valor);
                    else if (columnas[i] == "Desc.") AgregarPercepcion(result, "Desc", valor);
                    else if (columnas[i] == "Perc.") AgregarPercepcion(result, "Perc", valor);
                    else if (columnas[i] == "N.G") AgregarPercepcion(result, "NoGravado", valor);
                    else if (columnas[i] == "Impuestos") AgregarPercepcion(result, "Impuestos", valor);
                }

                result.ImporteNetoGravado = result.Subtotal;
                AvisarSiHayDiferenciaSinExplicar(result);
                return;
            }

            result.Advertencias.Add("No se pudieron extraer los totales del comprobante.");
        }

        // Si después de sumar Subtotal + Ivas + Percepciones sigue sin cerrar contra el Total,
        // hay algo que ninguna de las columnas conocidas explica -se avisa en vez de dejarlo
        // invisible, pero sin inventar en qué columna iría.
        static void AvisarSiHayDiferenciaSinExplicar(Factura result)
        {
            decimal resto = result.ImporteTotal - result.Subtotal - result.ImporteIva - result.Percepciones.Sum(p => p.Importe);
            if (Math.Abs(resto) >= 0.01m)
                result.Advertencias.Add($"Quedan ${resto:F2} del total sin explicar por Subtotal + IVA + Percepciones; revisar manualmente.");
        }
    }
}
