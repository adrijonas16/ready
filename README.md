# Ready - Útiles Escolares

Plataforma para compra de útiles escolares con generación automática de listas.

## Repositorios

- **Backend/API**: https://github.com/adrijonas16/ready
- **Frontend/Web**: https://github.com/adrijonas16/ready-web

## Tech Stack

- **Frontend**: Next.js (React) + TailwindCSS
- **Backend**: ASP.NET Core Web API (.NET 8)
- **Database**: PostgreSQL (Docker)
- **ORM**: Dapper

## Getting Started

### Backend
```bash
cd backend/UtilesApi
dotnet run
```

Frontend en submódulo:
```bash
git submodule update --init
cd frontend/utiles-web
npm run dev
```

## Estructura

- `backend/UtilesApi/` - API REST en ASP.NET Core
- `frontend/utiles-web/` - Frontend Next.js (submódulo)
## Lectura de listas con IA

Cuando un cliente sube la foto, PDF o Word de su lista, la API la lee con Claude (Anthropic)
y, en la misma pasada, elige del catálogo la opción económica, estándar y premium de cada útil.
El cliente ve el resultado en alrededor de un minuto en `/lists/{id}` y lo ajusta a su medida.

Configuración (en Render u otro hosting, como variable de entorno):

```bash
AI__ApiKey=sk-ant-...            # o ANTHROPIC_API_KEY
AI__Model=claude-sonnet-5-5      # opcional
```

- Sin clave, la lista queda en revisión manual (`ia_estado = MANUAL`). Si `OCR:Provider` es `mock`,
  se usa el lector de prueba de siempre (`ia_estado = DEMO`).
- Las imágenes deben pesar menos de 5 MB; el frontend ya las reduce antes de subirlas.
- `POST /api/lists/{id}/reprocess` vuelve a leer una lista.
- Las columnas nuevas se crean solas al arrancar (`ALTER TABLE ... ADD COLUMN IF NOT EXISTS`).

## Poner la tienda en producción

La API crea sola todas las tablas al arrancar y, si la base está vacía, carga el catálogo
(69 productos en soles), 4 colegios de ejemplo y 5 listas oficiales ya aprobadas.
Ese catálogo sale de `Infrastructure/Database/seed-catalogo.sql`, generado desde
`ready-web/src/demo/catalogo.ts`, así que coincide con la tienda de demostración.
Solo hace falta una base PostgreSQL vacía (Supabase, Railway, Neon…).

Variables en Render (Environment). **Ningún secreto va en `appsettings.json`**: ahí quedan vacíos.

```bash
ConnectionStrings__DefaultConnection=Host=...;Port=5432;Database=postgres;Username=...;Password=...;SSL Mode=Require;Trust Server Certificate=true
Jwt__Key=<secreto aleatorio de 32+ caracteres: openssl rand -base64 48>
Cors__Origins=https://ready-web-gamma.vercel.app,https://ready-web-rf43.vercel.app   # dominios del frontend, separados por comas
Admin__Email=tu-correo@dominio.com   # quien se registre con este correo entra como administrador
Storage__Provider=r2                 # o local; con r2: R2__AccountId, R2__AccessKeyId, R2__SecretAccessKey, R2__PublicUrl
AI__ApiKey=sk-ant-...                # sin esto, las listas subidas quedan en revisión manual
ASPNETCORE_ENVIRONMENT=Production    # es el valor por defecto; Swagger solo existe en Development
```

En tu equipo, los secretos van en `backend/UtilesApi/appsettings.Development.local.json` (git lo ignora), por ejemplo:

```json
{ "ConnectionStrings": { "DefaultConnection": "Host=localhost;Port=5434;Database=ready;Username=ready_user;Password=..." },
  "Jwt": { "Key": "<clave-local-de-32-caracteres-o-mas>" } }
```

En Development, si falta `Jwt:Key`, la API usa una clave aleatoria temporal (las sesiones se pierden al reiniciar).

En Vercel (proyecto `ready-web`):

```bash
NEXT_PUBLIC_MODO_DEMO=false
NEXT_PUBLIC_API_URL=https://<tu-servicio>.onrender.com/api
```

Los pagos siguen siendo de prueba: la tienda no está conectada a una pasarela real.

### Seguridad (JWT y roles)

- Toda la administración exige `Authorization: Bearer <token>` con rol `ADMIN` u `OPERATOR`
  (productos, variantes, imágenes, categorías, marcas, campañas, noticias, colegios, grados, secciones,
  revisión de listas, todos los pedidos, estadísticas y `PUT /api/settings`).
  Ver usuarios y cambiar roles es solo para `ADMIN`; nadie puede cambiar su propio rol.
- Siguen públicos el catálogo, colegios, listas oficiales, una lista por id, el flujo de lista del cliente
  (subir, copiar, ajustar útiles/plan/extras de **su** copia, observaciones) y `GET /api/settings`.
- Comprar (`POST /api/orders`), ver un pedido o los pedidos de un usuario y cancelar exigen sesión:
  solo el dueño (o ADMIN/OPERATOR). El pedido siempre es del usuario del token y el precio lo calcula la API.
- El primer administrador sigue siendo quien se registre con `Admin__Email`; los demás roles se asignan
  desde Admin > Usuarios.
- **`Jwt__Key` es obligatoria**: fuera de Development la API **no arranca** si falta, tiene menos de 32 caracteres
  o es una de las claves que estuvieron publicadas en el repositorio (se comparan por hash).
- Los avisos en tiempo real (SignalR, `/hub/notifications`) también usan el token: el frontend lo envía con
  `accessTokenFactory` y solo ADMIN/OPERATOR entran al grupo de administración.

### Lanzamiento: HTTPS, CORS, límites y anti-spam

**Rotar `Jwt__Key`.** La clave que estuvo en `appsettings.json` es pública (historial de git). Genera una nueva
(`openssl rand -base64 48`), guárdala en Render y vuelve a desplegar. Todas las sesiones abiertas se cierran
(los usuarios vuelven a iniciar sesión). Para rotarla otra vez más adelante, basta cambiar la variable.

**HTTPS.** Render termina TLS y pasa `X-Forwarded-Proto`/`X-Forwarded-For`; la API los lee
(`ForwardedHeaders__ForwardLimit`, por defecto 2: cliente → Cloudflare → Render) y, si llega `CF-Connecting-IP`,
usa esa IP como la del cliente (`ForwardedHeaders__UseCfConnectingIp=false` lo desactiva).
Fuera de Development: redirección a HTTPS (308) y `Strict-Transport-Security: max-age=63072000; includeSubDomains; preload`.
`/health` responde `200 {"status":"ok"}` sin redirección: úsalo como *Health Check Path* en Render.

**Cabeceras.** Todas las respuestas llevan `X-Content-Type-Options: nosniff`, `X-Frame-Options: DENY`,
`Referrer-Policy: no-referrer`, `Content-Security-Policy: default-src 'none'; frame-ancestors 'none'` y
`Cache-Control: no-store`; sin cabecera `Server`. Las imágenes subidas se sirven desde R2 (otro dominio), así que la CSP
no las afecta; con `Storage__Provider=local` se sirven en `/uploads` con una CSP propia (`sandbox`).
En producción los errores devuelven un JSON genérico (`SERVER_ERROR`), nunca el detalle ni el stack trace.

**CORS.** Solo los orígenes de `Cors__Origins` (por defecto los dos de Vercel y `http://localhost:3000` / `3005`).
Sin cookies (`AllowCredentials` apagado): el token va en `Authorization`, también en SignalR (`accessTokenFactory`).
Si cambias el dominio del frontend o agregas uno propio, agrégalo a `Cors__Origins`.

**Límites por IP** (en memoria; respuesta `429` con `Retry-After` y
`{ success:false, error:{ code:'RATE_LIMIT', message:'Demasiados intentos. Espera un momento.' } }`):

| Endpoint | Límite |
|---|---|
| `POST /api/auth/login` | 10 / minuto por IP, 10 / minuto por correo, y bloqueo de 15 min tras 5 contraseñas incorrectas en 15 min |
| `POST /api/auth/register` | 10 / minuto |
| `POST /api/lists/upload`, `POST /api/lists/from-text` | 5 / 10 minutos |
| `POST /api/lists/{id}/observaciones` | 30 / 10 minutos |
| `POST /api/lists/{id}/items` | 30 / 10 minutos |
| `POST /api/orders` | 10 / 10 minutos |
| Todo lo demás | 300 / minuto (`RateLimit__GlobalPerMinute`) |

`RateLimit__Enabled=false` los apaga (solo para pruebas). Con varias instancias, cada una cuenta por separado.

**Anti-spam.** Registro, observaciones, agregar útiles, pedidos y subida de listas aceptan dos campos más:
`sitioWeb` (campo oculto que debe ir vacío) y `_t` (milisegundos que el formulario estuvo abierto; menos de 2500 se
rechaza). En JSON van en el cuerpo; en la subida, como campos del formulario. Si no vienen se acepta (clientes antiguos).
Rechazo: `400 { code: 'SPAM_DETECTED' }`.

**Tamaños.** Cuerpo JSON hasta 2 MB (`Limits__MaxRequestBodyBytes`); subidas hasta 25 MB por petición, 10 MB por archivo,
6 archivos por lista (JPG, PNG, WEBP, HEIC, PDF o Word; se revisa la firma real del archivo). Imágenes del panel: JPG, PNG,
WEBP o GIF (sin SVG).

**Secretos en git.** `.github/workflows/gitleaks.yml` revisa cada push y PR con gitleaks (`.gitleaks.toml`).
`.gitleaksignore` lista hallazgos viejos ya tratados (por huella, sin el secreto).
