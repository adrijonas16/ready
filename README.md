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