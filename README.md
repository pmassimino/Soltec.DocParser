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
- **Autenticación con Soltec.Suscripcion**: cada cliente hace login en Soltec.Suscripcion y
  usa ese token; solo se atiende a cuentas con la suscripción vigente.

## Endpoints

| Método | Ruta | Descripción |
|---|---|---|
| POST | `/api/facturas/compra/pdf` | Procesa una factura en PDF |
| POST | `/api/facturas/compra/imagen` | Procesa una foto o escaneo (jpg, png, bmp, webp, tiff) |
| POST | `/api/facturas/compra/pagina` | Renderiza una página del PDF o de la imagen como PNG/JPG |
| GET  | `/api/health` | Health check (no requiere autenticación) |
| GET  | `/api/admin/uso` | Estadísticas de uso para el administrador (ver [Registro de uso](#registro-de-uso)) |

Los `POST` reciben el archivo como `multipart/form-data` y requieren el header
`Authorization: Bearer <token>`.

### Autenticación

1. El cliente hace `POST {Suscripcion}/api/login` con `{ "nombre": "...", "password": "..." }`
   (en SAE, el usuario y contraseña de `Empresas\SettingGlobal`) y recibe `{ "token": "..." }`.
   El token dura 1 día; al recibir un 401 hay que volver a hacer login.
2. DocParser valida el token localmente (firma, issuer, audience, vencimiento) con la misma
   `Jwt:Key` que Soltec.Suscripcion.
3. Luego consulta `GET {Suscripcion}/api/suscripcion/estado/plan` con el mismo token (cacheado
   `CacheMinutos` por usuario) y exige una suscripción en `ACTIVO` o `AVISO` del plan
   `IdPlanRequerido` (3 = Soltec.DocParse; 0 = cualquier plan).

| Respuesta | Motivo |
|-----------|--------|
| 401 | Sin token, token inválido o vencido |
| 403 | La cuenta no tiene la suscripción vigente |
| 503 | No se pudo consultar Soltec.Suscripcion |

### Parámetros de query

`/pdf` e `/imagen`:

| Parámetro | Valores | Descripción |
|---|---|---|
| `formato` | `xml` | Responde en XML para VFP. Si se omite, la respuesta es JSON. |
| `usarIaSiFalla` | `false` | El fallback por IA está activo por defecto si hay una API key configurada; con `false` se desactiva para esa llamada. Se activa cuando el parser no puede determinar número, punto de venta, letra, tipo, fecha, total, detalle o CUIT del proveedor o CUIT del receptor. |

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

## Registro de uso

Cada llamada a `/pdf` e `/imagen` queda registrada en una base SQLite (`Data/uso.db` por
defecto), haya salido bien o no:

- `RegistrosUso`: una fila por comprobante, con el cliente (`IdUsuario` y nombre del token), tipo
  de archivo (`PDF`/`IMAGEN`), resultado (`OK`, el código de error o `HTTP_400`), duración, si se
  delegó a la IA, qué proveedor lo resolvió y el total de tokens y costo de IA.
- `RegistrosUsoIa`: una fila por cada llamada a una API de IA (incluidos los reintentos y los
  proveedores que fallaron, que también se cobran), con modelo, tokens de entrada/caché/salida y
  costo en USD.

El costo se calcula al momento de la llamada con los precios de `Uso:PreciosIa` (USD por millón
de tokens, por modelo o prefijo de modelo). Si un modelo no tiene precio configurado se registra en
0 y queda un warning en el log. La base se crea y migra sola al arrancar la API.

`GET /api/admin/uso?desde=AAAA-MM-DD&hasta=AAAA-MM-DD` (ambas inclusive; por defecto los últimos
30 días) devuelve totales, uso por cliente (frecuencia, días activos, último uso, costo), por
proveedor/modelo de IA, por día, por hora del día y por resultado. Es para el administrador, no
para los clientes: no usa el JWT sino el header `X-Admin-Key` con el valor de `Uso:AdminApiKey`
(si está vacía, el endpoint responde 404).

```bash
curl "http://localhost:5037/api/admin/uso?desde=2026-10-01&hasta=2026-10-31" -H "X-Admin-Key: $ADMIN_KEY"
```

## Requisitos

- [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0)
- Datos de idioma de Tesseract en `tessdata/` (`spa.traineddata`, incluido en el repo)
- (Opcional) Una API key de [Anthropic](https://console.anthropic.com/) para el fallback por IA

## Configuración

En `appsettings.json`, o en `appsettings.Development.json` para desarrollo local:

```json
{
  "Suscripcion": {
    "UrlService": "https://host-de-suscripcion",
    "IdPlanRequerido": 3,
    "CacheMinutos": 10,
    "Jwt": {
      "Key": "la misma Jwt:Key de Soltec.Suscripcion",
      "Issuer": "Soltec.Suscripcion",
      "Audience": "Soltec.Suscripcion"
    }
  },
  "Anthropic": {
    "ApiKey": "",
    "Modelo": "claude-sonnet-5"
  },
  "ConnectionStrings": {
    "Uso": "Data Source=Data/uso.db"
  },
  "Uso": {
    "AdminApiKey": "",
    "ZonaHoraria": "America/Argentina/Buenos_Aires",
    "PreciosIa": {
      "claude-sonnet-5": { "EntradaPorMillon": 2.00, "EntradaCachePorMillon": 0.20, "SalidaPorMillon": 10.00 },
      "deepseek-flash": { "EntradaPorMillon": 0.30, "EntradaCachePorMillon": 0.006, "SalidaPorMillon": 1.20 }
    }
  }
}
```

Si `Anthropic:ApiKey` está vacía, el fallback por IA queda desactivado y la respuesta lo indica
en `Advertencias`.

> No subas API keys reales al repositorio. `appsettings.Development.json` ya está en
> `.gitignore`. En producción conviene usar variables de entorno (`Anthropic__ApiKey`, `Suscripcion__Jwt__Key`) o un
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
  -H "Authorization: Bearer $TOKEN" \
  -F "file=@factura.pdf"
```

El archivo `Soltec.DocParser.http` tiene requests de ejemplo para Visual Studio o VS Code.

## Estructura

```
Endpoints/   Definición de los endpoints (Minimal API)
Models/      Modelo de salida (Factura, ítems, importes)
Services/    Extracción (PdfPig, OCR, QR), parsers por formato, fallback por IA, serializador XML para VFP
Tenancy/     Autenticación contra Soltec.Suscripcion (JWT + estado de suscripción)
Uso/         Registro de uso por cliente (EF Core + SQLite) y estadísticas para el administrador
Clientes/    Cliente de ejemplo en Visual FoxPro
tessdata/    Datos de idioma de Tesseract
```

## Licencia

<!-- Completar: MIT, Apache-2.0, propietaria, etc. -->
