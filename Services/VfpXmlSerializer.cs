using System.Globalization;
using System.Text;
using System.Xml;
using Soltec.DocParser.Models;

namespace Soltec.DocParser.Services
{
    // Serializa el resultado para un cliente VFP 8, que no tiene parser JSON nativo. Cada tabla
    // va en su propio bloque <VFPData>, con el mismo formato que genera CURSORTOXML() (esquema
    // XSD inline), todos dentro de un <DocParser>:
    //
    //     FOR lnI = 1 TO OCCURS("<VFPData>", lcXml)
    //         XMLTOCURSOR("<VFPData>" + STREXTRACT(lcXml, "<VFPData>", "</VFPData>", lnI) + "</VFPData>")
    //     ENDFOR
    //
    // (ver Clientes/VFP/docparser.prg, que ya lo resuelve)
    // deja los cursores "resultado", "cabecera", "detalle", "ivas", "tributos" y "advert" ya
    // tipados (C/N/D/L/M). Un solo DataSet con todas las tablas sería lo natural para
    // XMLAdapter, pero en VFP 8 XMLAdapter.LoadXML exige MSXML4 SP1, que está discontinuado y
    // no viene con Windows; XMLTOCURSOR usa el MSXML3 del sistema y funciona en cualquier PC,
    // pero carga una sola tabla por documento -de ahí un bloque por tabla. El esquema se emite
    // explícito (largo y decimales por campo) porque sin esquema VFP trae todo como texto. Los
    // nombres de campo tienen hasta 10 caracteres para que sobrevivan un COPY TO a tabla libre.
    public static class VfpXmlSerializer
    {
        // Mismo encoding que usa VFP para su propio XML; los acentos y la ñ llegan tal cual.
        public static readonly Encoding Encoding;

        static VfpXmlSerializer()
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            Encoding = Encoding.GetEncoding(1252);
        }

        enum Tipo { C, N, D, L, M }

        record Campo(string Nombre, Tipo Tipo, int Ancho = 0, int Decimales = 0);

        record Tabla(string Nombre, Campo[] Campos, List<object?[]> Filas);

        static Campo C(string n, int ancho) => new(n, Tipo.C, ancho);
        static Campo N(string n, int ancho, int dec) => new(n, Tipo.N, ancho, dec);
        static Campo D(string n) => new(n, Tipo.D);
        static Campo L(string n) => new(n, Tipo.L);
        static Campo M(string n) => new(n, Tipo.M);

        static readonly Campo[] CamposResultado =
        {
            L("ok"), C("error", 40), M("mensaje"),
        };

        static readonly Campo[] CamposCabecera =
        {
            C("tipocmp", 40), C("codcmp", 3), C("letra", 1), C("ptovta", 5), C("numero", 8),
            D("fecha"), D("fvtopago"), D("perdesde"), D("perhasta"), C("condventa", 60),
            C("prvcuit", 11), C("prvrsoc", 100), C("prvdomic", 150), C("prvciva", 40), C("prviibb", 20),
            C("reccuit", 11), C("recrsoc", 100),
            N("subtotal", 15, 2), N("netograv", 15, 2), N("impiva", 15, 2), N("otrostrib", 15, 2), N("total", 15, 2),
            C("cae", 14), D("fvtocae"), L("porocr"), L("poria"),
        };

        static readonly Campo[] CamposDetalle =
        {
            N("item", 4, 0), C("codigo", 30), C("concepto", 254), N("cantidad", 15, 4), C("unidad", 20),
            N("precio", 15, 4), N("porcbonif", 7, 2), N("impbonif", 15, 2), N("subtotal", 15, 2),
            N("alicuota", 6, 2), N("subtotiva", 15, 2), C("ctg", 11), N("peso", 15, 2), N("tarifa", 15, 4),
        };

        static readonly Campo[] CamposImporte =
        {
            C("id", 60), N("importe", 15, 2),
        };

        static readonly Campo[] CamposAdvertencia =
        {
            N("item", 4, 0), M("texto"),
        };

        public static string Serializar(Factura f)
        {
            var tablas = new List<Tabla>
            {
                new("resultado", CamposResultado, new() { new object?[] { true, "", "" } }),
                new("cabecera", CamposCabecera, new()
                {
                    new object?[]
                    {
                        f.TipoComprobante, f.CodigoComprobante, f.Letra, f.PuntoVenta, f.Numero,
                        f.FechaEmision, f.FechaVencimientoPago, f.PeriodoFacturadoDesde, f.PeriodoFacturadoHasta, f.CondicionVenta,
                        f.ProveedorCuit, f.ProveedorRazonSocial, f.ProveedorDomicilio, f.ProveedorCondicionIva, f.ProveedorIngresosBrutos,
                        f.ReceptorCuit, f.ReceptorRazonSocial,
                        f.Subtotal, f.ImporteNetoGravado, f.ImporteIva, f.TotalOtrosTributos, f.ImporteTotal,
                        f.Cae, f.FechaVtoCae, f.ObtenidoPorOcr, f.ObtenidoPorIa,
                    },
                }),
                new("detalle", CamposDetalle, f.Detalle.Select((d, i) => new object?[]
                {
                    i + 1, d.Codigo, d.Concepto, d.Cantidad, d.UnidadMedida,
                    d.PrecioUnitario, d.PorcentajeBonificacion, d.ImporteBonificacion, d.Subtotal,
                    d.AlicuotaIva, d.SubtotalConIva, d.Ctg, d.Peso, d.Tarifa,
                }).ToList()),
                new("ivas", CamposImporte, f.Ivas.Select(x => new object?[] { x.Id, x.Importe }).ToList()),
                new("tributos", CamposImporte, f.OtrosTributos.Select(x => new object?[] { x.Id, x.Importe }).ToList()),
                new("advert", CamposAdvertencia, f.Advertencias.Select((a, i) => new object?[] { i + 1, a }).ToList()),
            };
            return Escribir(tablas);
        }

        // Para los casos en que no hay factura que devolver (PDF sin texto, OCR sin resultado):
        // el cliente VFP siempre recibe el cursor "resultado" y revisa resultado.ok antes de
        // buscar los demás.
        public static string SerializarError(string error, string mensaje) =>
            Escribir(new List<Tabla>
            {
                new("resultado", CamposResultado, new() { new object?[] { false, error, mensaje } }),
            });

        const string Xsd = "http://www.w3.org/2001/XMLSchema";
        const string MsData = "urn:schemas-microsoft-com:xml-msdata";
        const string RaizTabla = "VFPData";

        static string Escribir(List<Tabla> tablas)
        {
            var sb = new StringBuilder();
            var settings = new XmlWriterSettings { Indent = true, IndentChars = "\t", OmitXmlDeclaration = true };
            using (var w = XmlWriter.Create(sb, settings))
            {
                w.WriteStartElement("DocParser");
                foreach (var t in tablas)
                    EscribirTabla(w, t);
                w.WriteEndElement();
            }

            return "<?xml version = \"1.0\" encoding=\"Windows-1252\" standalone=\"yes\"?>\r\n" + sb;
        }

        // Un bloque idéntico al que genera CURSORTOXML(alias, ..., 1, 0, 0, "1") para un cursor.
        static void EscribirTabla(XmlWriter w, Tabla t)
        {
            w.WriteStartElement(RaizTabla);

            w.WriteStartElement("xsd", "schema", Xsd);
            w.WriteAttributeString("id", RaizTabla);
            w.WriteAttributeString("xmlns", "msdata", null, MsData);
            w.WriteStartElement("element", Xsd);
            w.WriteAttributeString("name", RaizTabla);
            w.WriteAttributeString("IsDataSet", MsData, "true");
            w.WriteStartElement("complexType", Xsd);
            w.WriteStartElement("choice", Xsd);
            w.WriteAttributeString("maxOccurs", "unbounded");
            w.WriteStartElement("element", Xsd);
            w.WriteAttributeString("name", t.Nombre);
            w.WriteAttributeString("minOccurs", "0");
            w.WriteAttributeString("maxOccurs", "unbounded");
            w.WriteStartElement("complexType", Xsd);
            w.WriteStartElement("sequence", Xsd);
            foreach (var c in t.Campos)
                EscribirCampoXsd(w, c, Xsd);
            w.WriteEndElement(); // sequence
            w.WriteEndElement(); // complexType
            w.WriteEndElement(); // element (tabla)
            w.WriteEndElement(); // choice
            w.WriteStartElement("anyAttribute", Xsd);
            w.WriteAttributeString("namespace", "http://www.w3.org/XML/1998/namespace");
            w.WriteAttributeString("processContents", "lax");
            w.WriteEndElement();
            w.WriteEndElement(); // complexType
            w.WriteEndElement(); // element (raíz)
            w.WriteEndElement(); // schema

            foreach (var fila in t.Filas)
            {
                w.WriteStartElement(t.Nombre);
                for (int i = 0; i < t.Campos.Length; i++)
                    w.WriteElementString(t.Campos[i].Nombre, FormatearValor(t.Campos[i], fila[i]));
                w.WriteEndElement();
            }

            w.WriteEndElement(); // VFPData
        }

        static void EscribirCampoXsd(XmlWriter w, Campo c, string xsd)
        {
            w.WriteStartElement("element", xsd);
            w.WriteAttributeString("name", c.Nombre);
            switch (c.Tipo)
            {
                case Tipo.D:
                    w.WriteAttributeString("type", "xsd:date");
                    break;
                case Tipo.L:
                    w.WriteAttributeString("type", "xsd:boolean");
                    break;
                default:
                    w.WriteStartElement("simpleType", xsd);
                    w.WriteStartElement("restriction", xsd);
                    if (c.Tipo == Tipo.N)
                    {
                        // VFP describe un N(15,2) como totalDigits=14 (no cuenta el punto decimal).
                        w.WriteAttributeString("base", "xsd:decimal");
                        EscribirFaceta(w, xsd, "totalDigits", c.Decimales > 0 ? c.Ancho - 1 : c.Ancho);
                        EscribirFaceta(w, xsd, "fractionDigits", c.Decimales);
                    }
                    else
                    {
                        // Un string con maxLength = int.MaxValue es como VFP marca un campo memo.
                        w.WriteAttributeString("base", "xsd:string");
                        EscribirFaceta(w, xsd, "maxLength", c.Tipo == Tipo.M ? int.MaxValue : c.Ancho);
                    }
                    w.WriteEndElement(); // restriction
                    w.WriteEndElement(); // simpleType
                    break;
            }
            w.WriteEndElement();
        }

        static void EscribirFaceta(XmlWriter w, string xsd, string nombre, int valor)
        {
            w.WriteStartElement(nombre, xsd);
            w.WriteAttributeString("value", valor.ToString(CultureInfo.InvariantCulture));
            w.WriteEndElement();
        }

        static string FormatearValor(Campo c, object? valor)
        {
            switch (c.Tipo)
            {
                case Tipo.D:
                    // Fecha vacía = elemento vacío, igual que VFP con {}.
                    return valor is DateTime d ? d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) : "";
                case Tipo.L:
                    return valor is true ? "true" : "false";
                case Tipo.N:
                    decimal n = valor switch
                    {
                        decimal x => x,
                        int x => x,
                        _ => 0m,
                    };
                    return Math.Round(n, c.Decimales).ToString("F" + c.Decimales, CultureInfo.InvariantCulture);
                case Tipo.C:
                    // Un valor más largo que el campo haría fallar la carga del cursor en VFP.
                    string s = LimpiarTexto(valor as string);
                    return s.Length > c.Ancho ? s[..c.Ancho] : s;
                default:
                    return LimpiarTexto(valor as string);
            }
        }

        // Caracteres de control (no válidos en XML 1.0) que a veces trae el texto extraído de un
        // PDF; se reemplazan por espacio para no romper el documento.
        static string LimpiarTexto(string? s) =>
            string.IsNullOrEmpty(s) ? "" : new string(s.Select(ch => XmlConvert.IsXmlChar(ch) ? ch : ' ').ToArray());
    }
}
