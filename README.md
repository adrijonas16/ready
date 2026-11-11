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

## Copias por alumno y consultas de productos

**Copias por alumno.** Una familia puede armar varias copias de la misma lista del colegio (una por hijo).
El nombre del alumno va en `extras.etiquetas.nombre` y separa la lista en el carrito y en el pedido (`order_items.para`).
`DELETE /api/lists/{id}/copia` borra una copia propia: solo copias (`copia_de` no nulo) y, si la copia es de una cuenta,
solo su dueño o ADMIN/OPERATOR. Los pedidos ya hechos no se tocan; solo pierden la referencia a la lista.

**Consultas de productos** (tabla `product_inquiries`). Si un cliente no encuentra un útil, pregunta si lo podemos
conseguir y se le responde sí o no en menos de 24 horas.

| Endpoint | Quién | Qué hace |
|---|---|---|
| `POST /api/consultas` | público (10 por hora por IP, anti-spam) | `{ producto, detalle?, nombre?, contacto?, supplyListId? }`. Sin sesión, `nombre` y `contacto` (correo o celular de 9 dígitos) son obligatorios. Máximo 10 sin responder por persona. |
| `GET /api/consultas/mias` | cliente con sesión | Sus consultas. |
| `GET /api/consultas/{id}` | quien tiene el id | Sin sesión, el id lo guarda el navegador; si la consulta es de una cuenta, solo la ve su dueño o la tienda. |
| `GET /api/consultas?estado=` | ADMIN/OPERATOR | Pendientes primero (la más antigua arriba). `estado`: `PENDIENTE`, `DISPONIBLE` o `NO_DISPONIBLE`. |
| `PUT /api/consultas/{id}/respuesta` | ADMIN/OPERATOR | `{ disponible: true/false, respuesta? }`. Se puede corregir después. Avisa al cliente con cuenta (SignalR `InquiryAnswered`). |

Cada consulta nueva avisa al panel (SignalR `NewInquiry`). Se responde en Admin > Consultas; el cliente ve la respuesta en `/consultas`.

**Descripción de las opciones.** `product_variant_values.description` (hasta 1000 caracteres) se edita en
Admin > Productos > Variantes y se muestra en la ficha del producto al elegir esa opción.

## Colegios de Arequipa (padrón MINEDU)

`Infrastructure/Database/seed-colegios-arequipa.sql` trae los 187 colegios de educación básica regular del distrito de
Arequipa (padrón del MINEDU, enero de 2026) con un grado por cada nivel que ofrecen: inicial de 3, 4 y 5 años,
1.º a 6.º de primaria y 1.º a 5.º de secundaria (1580 grados, del año escolar en curso). Las sedes con el mismo nombre
(p. ej. De La Salle primaria y secundaria) quedan como un solo colegio. Se guarda el código modular, la gestión y,
cuando el padrón la tiene, la dirección y la ubicación.

La API lo carga sola al arrancar, **una sola vez** por base (queda anotado en `seed_runs`), dentro de una transacción.
Solo agrega lo que falta: no cambia ni borra colegios o grados existentes, y si el admin borra uno no vuelve a aparecer.
Los teléfonos y correos publicados (Excel `Colegios_Arequipa_ciudad_2026_1.xlsx`) se cargan igual, una sola vez, con
`seed-contactos-colegios.sql` (`scripts/generar_seed_contactos.py`): WhatsApp = primer celular, teléfono = primer fijo,
los demás números en las notas; solo completa campos vacíos.
Para regenerarlo desde el Excel: `python3 scripts/generar_seed_colegios.py Colegios_Arequipa_ciudad_2026.xlsx`
(y cambiar el nombre de la marca en `DatabaseInitializer` si debe volver a correr en bases que ya lo cargaron).

## Pedir las listas a los colegios (Admin > Pedir listas)

Cada colegio tiene sus datos de contacto (WhatsApp, teléfono, correo, nombre del contacto, notas) y un seguimiento:
sin contactar → contactado → respondió → lista recibida (o no interesado), con la fecha del último contacto y del próximo
recordatorio. Todo queda en `school_contact_log`.

- **WhatsApp con un clic**: abre WhatsApp con el mensaje ya escrito (o el recordatorio) y anota el contacto.
- **Correo**: por colegio o por lotes, y **recordatorios automáticos** cada hora a los que no respondieron
  (cada N días, hasta un máximo; se configura en el panel). Se activa con estas variables en Railway:

  ```bash
  Email__ResendApiKey=re_...                 # clave de https://resend.com
  Email__From=Ready <listas@tudominio.pe>    # remitente de un dominio verificado en Resend
  Email__ReplyTo=ventas@tudominio.pe         # opcional
  ```

  Sin esas variables el panel funciona igual (WhatsApp y llamadas) y avisa que el correo no está activo.
- **Llamadas**: botón para llamar, el mensaje como guion y dónde anotar el resultado.
- **Enlace del colegio**: cada mensaje lleva `/colegio/{token}` (128 bits aleatorios), donde el colegio sube la lista
  de cada grado sin crear cuenta. La lista entra a revisión y el colegio pasa a "lista recibida".
  `App__WebUrl` define la dirección de la tienda para armar el enlace (por defecto `https://ready-web-gamma.vercel.app`).

| Endpoint | Quién |
|---|---|
| `GET /api/contacto-colegios`, `PUT /api/contacto-colegios/{id}` | ADMIN/OPERATOR |
| `POST /api/contacto-colegios/{id}/mensaje` (enlace y mensajes armados), `POST …/{id}/registro`, `GET …/{id}/historial` | ADMIN/OPERATOR |
| `POST /api/contacto-colegios/correo` `{ ids, recordatorio }`, `POST /api/contacto-colegios/recordatorios` | ADMIN/OPERATOR (503 si el correo no está configurado) |
| `GET/PUT /api/contacto-colegios/config` (mensaje, recordatorio, asunto, días, máximo, automático) | ADMIN/OPERATOR |
| `GET /api/colegio-enlace/{token}`, `POST /api/colegio-enlace/{token}/lista` (multipart: `files`, `grado`, `nombre?`) | público (límite de subidas y anti-spam) |

## Poner la tienda en producción

La API y su base PostgreSQL están en **Railway** (proyecto `ready`); la web, en **Vercel** (`ready-web` y `ready-web-rf43`).
La API crea sola todas las tablas al arrancar y, si la base está vacía, carga el catálogo
(69 productos en soles), 4 colegios de ejemplo y 5 listas oficiales ya aprobadas, más los colegios del padrón
(ver más abajo). El catálogo sale de `Infrastructure/Database/seed-catalogo.sql`, generado desde
`ready-web/src/demo/catalogo.ts`, así que coincide con la tienda de demostración.

> La base anterior era una base gratis de Render; Render las borra a los 30 días y se perdió. No uses bases gratis que vencen.

Servicio de la API en Railway: repo `adrijonas16/ready`, rama `master`, *Root Directory* `/backend/UtilesApi` (Dockerfile),
*Healthcheck Path* `/health`, un volumen montado en `/data` para las imágenes subidas. Variables
(**ningún secreto va en `appsettings.json`**):

```bash
DATABASE_URL=${{Postgres.DATABASE_URL}}   # referencia a la base del mismo proyecto (también vale ConnectionStrings__DefaultConnection)
Jwt__Key=${{secret(64)}}                  # Railway la genera; cambiarla cierra todas las sesiones
Admin__Email=adrijonas16@gmail.com        # quien se registre con este correo entra como administrador
ASPNETCORE_ENVIRONMENT=Production         # Swagger solo existe en Development
PORT=10000
Storage__Provider=local                   # o r2: R2__AccountId, R2__AccessKeyId, R2__SecretAccessKey, R2__PublicUrl
Storage__LocalPath=/data/uploads
Storage__BaseUrl=https://${{RAILWAY_PUBLIC_DOMAIN}}/uploads
AI__ApiKey=sk-ant-...                     # opcional: sin esto, las listas subidas quedan en revisión manual
Cors__Origins=...                         # opcional: por defecto los dos dominios de Vercel y localhost
```

En tu equipo, los secretos van en `backend/UtilesApi/appsettings.Development.local.json` (git lo ignora), por ejemplo:

```json
{ "ConnectionStrings": { "DefaultConnection": "Host=localhost;Port=5434;Database=ready;Username=ready_user;Password=..." },
  "Jwt": { "Key": "<clave-local-de-32-caracteres-o-mas>" } }
```

En Development, si falta `Jwt:Key`, la API usa una clave aleatoria temporal (las sesiones se pierden al reiniciar).

En Vercel (proyectos `ready-web` y `ready-web-rf43`):

```bash
NEXT_PUBLIC_MODO_DEMO=false   # sin esto la web funciona en modo demo (todo en el navegador, sin API)
NEXT_PUBLIC_API_URL=https://ready-production-42f8ecea.up.railway.app/api
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

**Rotar `Jwt__Key`.** La clave que estuvo en `appsettings.json` es pública (historial de git); en Railway se usa una
generada (`${{secret(64)}}`). Para rotarla, cámbiala en Railway y vuelve a desplegar. Todas las sesiones abiertas se cierran
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
