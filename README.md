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

Variables en Render:

```bash
ConnectionStrings__DefaultConnection=Host=...;Port=5432;Database=postgres;Username=...;Password=...;SSL Mode=Require;Trust Server Certificate=true
Jwt__Key=<texto aleatorio de 32 caracteres o más>
Admin__Email=tu-correo@dominio.com   # quien se registre con este correo entra como administrador
Storage__Provider=local              # o s3 con sus credenciales
AI__ApiKey=sk-ant-...                # sin esto, las listas subidas quedan en revisión manual
```

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
- **Define `Jwt__Key` con un secreto propio**: con la clave del repositorio cualquiera podría firmar un
  token de administrador (la API lo avisa en el log al arrancar).
- Los avisos en tiempo real (SignalR, `/hub/notifications`) también usan el token: el frontend lo envía con
  `accessTokenFactory` y solo ADMIN/OPERATOR entran al grupo de administración.
