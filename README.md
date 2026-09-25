# Soltec.DocParser

API REST en .NET 8 para digitalizar **facturas de compra argentinas**. Recibe el comprobante
en PDF o como foto y devuelve los datos estructurados: cabecera, emisor, receptor, ítems, IVA
por alícuota, otros tributos, totales y CAE. La salida viene lista para importar en un sistema
de gestión.

## Características

- **PDF con texto embebido**: el texto se extrae junto con la posición de cada palabra usando
  [PdfPig](https://github.com/UglyToad/PdfPig).
- **Imágenes y escaneos**: OCR con [Tesseract](https://github.com/charlesw/tesseract). Antes de
  reconocer, la imagen se amplía y se limpia de ruido.
- **Varios formatos de factura**, detectados por su estructura y no por el emisor:
  - ARCA/AFIP estándar
  - "Gravado / Exento"
  - "SUB TOTAL / TOTAL"
  - "NETO / IVA / NO GRAVADO / OTROS IMPUESTOS / TOTAL"
  - SAE (con sus variantes de layout)

  Si el formato es desconocido, un parser genérico extrae lo que puede y deja una advertencia
  explícita por cada campo que no encuentra, en lugar de rechazar el comprobante.
- **QR de AFIP/ARCA**: si el comprobante trae el QR, se usa como fuente principal para número,
  punto de venta, CUIT, fecha, importe total y CAE. Cuando el texto no coincide con el QR, se
  agrega una advertencia.
- **Fallback por IA (opcional)**: si el parser por reglas no logra extraer ítems ni totales, el
  documento se puede enviar a Claude (Anthropic). El resultado queda marcado con `ObtenidoPorIa`.
- **Enriquecimiento de ítems**: detecta CTG, peso y tarifa dentro de las descripciones, por
  ejemplo en fletes de cereal.
- **Salida en JSON o XML**: con `?formato=xml` devuelve un DataSet XML que Visual FoxPro carga
  directamente con `XMLAdapter`.
- **Render de páginas**: devuelve una página del comprobante como PNG o JPG, para mostrarla
  junto a los datos extraídos en un formulario de revisión.
- **Multi-tenant**: cada cliente se autentica con su propia API key.

## Endpoints

| Método | Ruta | Descripción |
|---|---|---|
| POST | `/api/facturas/compra/pdf` | Procesa una factura en PDF |
| POST | `/api/facturas/compra/imagen` | Procesa una foto o escaneo (jpg, png, bmp, webp, tiff) |
| POST | `/api/facturas/compra/pagina` | Renderiza una página del PDF o de la imagen como PNG/JPG |
| GET  | `/api/health` | Health check (no requiere autenticación) |

Los `POST` reciben el archivo como `multipart/form-data` y requieren el header `ApiKey`.

### Parámetros de query

`/pdf` e `/imagen`:

| Parámetro | Valores | Descripción |
|---|---|---|
| `formato` | `xml` | Responde en XML para VFP. Si se omite, la respuesta es JSON. |
| `usarIaSiFalla` | `true` | Activa el fallback por IA cuando el parser por reglas no extrae nada útil. |

`/pagina`:

| Parámetro | Default | Descripción |
|---|---|---|
| `pagina` | `1` | Número de página del PDF (base 1) |
| `dpi` | `150` | Resolución del render del PDF (50 a 300) |
| `formato` | `png` | `png` o `jpg` |
| `anchoMaximo` | `1600` | Solo para imágenes: ancho máximo en píxeles |

La cantidad total de páginas se informa en el header de respuesta `X-Total-Paginas`.

### Respuesta

Además de los datos del comprobante, cada respuesta incluye:

- `Advertencias`: todo lo que no se pudo extraer o que conviene revisar a mano.
- `ObtenidoPorOcr`: `true` si el texto salió de OCR y no de un PDF con texto embebido.
- `ObtenidoPorIa`: `true` si el resultado viene del fallback por IA.

La API **no valida** si el comprobante corresponde al cliente que lo envía. Extrae los CUIT del
emisor y del receptor, y la decisión de si se trata de una compra queda del lado del sistema
que consume la API.

## Requisitos

- [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0)
- Datos de idioma de Tesseract en `tessdata/` (`spa.traineddata`, incluido en el repo)
- (Opcional) Una API key de [Anthropic](https://console.anthropic.com/) para el fallback por IA

## Configuración

En `appsettings.json`, o en `appsettings.Development.json` para desarrollo local:

```json
{
  "Tenants": [
    { "ApiKey": "tu-api-key", "Nombre": "Nombre del cliente" }
  ],
  "Anthropic": {
    "ApiKey": "",
    "Modelo": "claude-sonnet-5"
  }
}
```

Si `Anthropic:ApiKey` está vacía, el fallback por IA queda desactivado y la respuesta lo indica
en `Advertencias`.

> No subas API keys reales al repositorio. `appsettings.Development.json` ya está en
> `.gitignore`. En producción conviene usar variables de entorno (`Anthropic__ApiKey`) o un
> gestor de secretos.

## Ejecución

```bash
dotnet run
```

La API queda escuchando en `http://localhost:5037` (y en `https://localhost:7099` con el perfil
https). En desarrollo, Swagger UI está disponible en `/swagger`.

Ejemplo:

```bash
curl -X POST "http://localhost:5037/api/facturas/compra/pdf?formato=xml" \
  -H "ApiKey: tu-api-key" \
  -F "file=@factura.pdf"
```

El archivo `Soltec.DocParser.http` tiene requests de ejemplo para Visual Studio o VS Code.

## Estructura

```
Endpoints/   Definición de los endpoints (Minimal API)
Models/      Modelo de salida (Factura, ítems, importes)
Services/    Extracción (PdfPig, OCR, QR), parsers por formato, fallback por IA, serializador XML para VFP
Tenancy/     Autenticación por API key y configuración de tenants
Clientes/    Cliente de ejemplo en Visual FoxPro
tessdata/    Datos de idioma de Tesseract
```

## Licencia

<!-- Completar: MIT, Apache-2.0, propietaria, etc. -->
