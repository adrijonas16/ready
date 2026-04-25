# Útiles Escolares - Especificación Técnica

## 1. Visión General del Proyecto

**Nombre**: Útiles Platform  
**Tipo**: E-commerce híbrido (listas escolares + catálogo tradicional)  
**APIs**: RESTful API-First designed para Web + Mobile  
**Stack**: Next.js, ASP.NET Core Web API, Dapper, PostgreSQL, AWS S3, Azure Vision OCR

---

## 2. Arquitectura del Sistema

### 2.1 Estructura de Proyectos

```
/
├── frontend/                 # Next.js 14 (App Router)
│   ├── src/
│   │   ├── app/             # Pages y APIs
│   │   ├── components/      # Componentes React
│   │   ├── lib/             #Utilities
│   │   └── services/         # API clients
│   └── package.json
├── backend/                  # ASP.NET Core 8 Web API
│   ├── Api/                 # Controllers
│   ├── Core/                # Domain entities, interfaces
│   ├── Infrastructure/      # Database, S3, OCR
│   └── Services/             # Business logic
└── SPEC.md
```

### 2.2 Modelo de Datos

#### Users
- id (UUID, PK)
- email (string, unique)
- password_hash (string)
- name (string)
- phone (string, nullable)
- address (text, nullable)
- role (enum: USER, ADMIN, OPERATOR)
- created_at (timestamp)
- updated_at (timestamp)

#### Schools
- id (UUID, PK)
- name (string)
- address (text, nullable)
- created_at (timestamp)

#### Grades
- id (UUID, PK)
- school_id (UUID, FK)
- name (string)  -- "1° Básico", "2° Medio", etc.
- year (integer)
- created_at (timestamp)

#### SupplyLists
- id (UUID, PK)
- user_id (UUID, FK, nullable para listas oficiales)
- school_id (UUID, FK)
- grade_id (UUID, FK)
- year (integer)
- image_url (string)
- ocr_text (text, nullable)
- parsed_college (string, nullable)
- parsed_grade (string, nullable)
- estado (enum: PENDIENTE_REVISION, EN_REVISION, OBSERVADA, VALIDADA, PROCESADA)
- es_oficial (boolean, default: false)
- observaciones (text, nullable)
- submitted_by (string, nullable)
- fecha_subida (timestamp)
- fecha_inicio_revision (timestamp, nullable)
- fecha_validacion (timestamp, nullable)
- created_at (timestamp)
- updated_at (timestamp)

#### SupplyItems
- id (UUID, PK)
- supply_list_id (UUID, FK)
- product_id (UUID, FK, nullable)
- nombre_original (string)
- nombre_detectado (string)
- cantidad (integer)
- notas (text, nullable)
- matched_product_id (UUID, FK, nullable)
- matched_quantity (integer, nullable)
- price_at_match (decimal, nullable)
- created_at (timestamp)
- updated_at (timestamp)

#### Products
- id (UUID, PK)
- name (string)
- description (text, nullable)
- category (string)
- brand (string, nullable)
- sku (string, unique)
- base_price (decimal)
- image_url (string)
- stock (integer)
- attributes (JSONB, nullable)  -- {"color": "rojo", "tipo": "espiral"}
- is_active (boolean)
- created_at (timestamp)
- updated_at (timestamp)

#### AdditionalCosts
- id (UUID, PK)
- keyword (string)
- description (string)
- cost (decimal)
- is_active (boolean)

#### Orders
- id (UUID, PK)
- user_id (UUID, FK)
- supply_list_id (UUID, FK, nullable)
- total (decimal)
- status (enum: RECIBIDO, EN_PREPARACION, ARMADO, EN_CAMINO, ENTREGADO)
- shipping_address (text)
- shipping_phone (string)
- tracking_number (string, nullable)
- created_at (timestamp)
- updated_at (timestamp)

#### OrderItems
- id (UUID, PK)
- order_id (UUID, FK)
- product_id (UUID, FK)
- quantity (integer)
- unit_price (decimal)
- notes (text, nullable)
- created_at (timestamp)

#### OrderStatusHistory
- id (UUID, PK)
- order_id (UUID, FK)
- status (string)
- notes (text, nullable)
- changed_by (UUID, FK)
- created_at (timestamp)

---

## 3. API Specification

### 3.1 Endpoints Principales

#### Lists
- `POST /api/lists/upload` - Subir lista (imagen)
- `GET /api/lists/{id}` - Obtener estado de lista
- `GET /api/lists` - Listar listas (con filtros)
- `PUT /api/lists/{id}` - Actualizar lista (edición)
- `PUT /api/lists/{id}/status` - Cambiar estado
- `POST /api/lists/{id}/approve` - Aprobar lista (admin)
- `POST /api/lists/{id}/observe` - Marcar como observada

#### Products
- `GET /api/products` - Catálogo completo
- `GET /api/products/{id}` - Detalle producto
- `GET /api/products/search?q=` - Buscar productos

#### Orders
- `POST /api/orders` - Crear orden
- `GET /api/orders/{id}` - Estado orden
- `GET /api/orders` - Historial usuario

#### Schools
- `GET /api/schools` -Lista escuelas
- `GET /api/schools/{id}/grades` - Grados por escuela

### 3.2 Formatos de Respuesta

```json
// Success
{"success": true, "data": {...}}

// Error
{"success": false, "error": {"code": "ERR_CODE", "message": "..."}}
```

---

## 4. Frontend - Estructura de Pages

### 4.1 Rutas

```
/                           # Home
/login                      # Login
/register                   # Register
/dashboard                  # User dashboard
/upload                     # Subir lista
/lists                      # Mis listas
/lists/[id]                 # Detalle lista
/cart                       # Carrito
/checkout                   # Checkout
/admin                      # Panel admin
/admin/lists                # Gestión listas
/admin/lists/review         # Revisión
/admin/products             # Gestión productos
/admin/settings             # Configuración
```

### 4.2 Componentes Principales

- ImageUploader - Validación y subida de imagen
- ListStatusBadge - Indicator de estado
- ProductCard - Tarjeta producto
- ProductMatcher - Matching fuzzy
- CartItem - Item en carrito
- OrderTracker - Seguimiento pedido

---

## 5. Flujo de Estados

### 5.1 Lista de Útiles

```
PENDIENTE_REVISION
    ↓ (admin inicia revisión)
EN_REVISION
    ↓ (admin encuentra problemas)
OBSERVADA  → usuario puede ver observación
    ↓ (admin re-procesa)
EN_REVISION
    ↓ (admin approves)
VALIDADA
    ↓ (sistema procesa matching)
PROCESADA
```

### 5.2 Pedido

```
RECIBIDO
    ↓
EN_PREPARACION
    ↓
ARMADO
    ↓
EN_CAMINO
    ↓
ENTREGADO
```

---

## 6. Validación de Imagen (Frontend)

### 6.1 Criterios

- Resolución mínima: 1024x1024 px
- Blur detection: Laplacian variance > 100
- Iluminación: Brillo promedio > 50

### 6.2 Librerías

- react-image-blur (detección de blur)
- ExifReader (metadatos)

---

## 7. OCR y Matching

### 7.1 OCR

- Azure Vision API o Google Cloud Vision
-Extracción de texto
- Parser de estructura

### 7.2 Matching

- Fuzzy search con Fuse.js (backend scoring)
- Matching por:
  - Nombre producto (weight: 0.5)
  - Categoría (weight: 0.3)
  - Atributos (weight: 0.2)

---

## 8. Configuración Adicional

### 8.1 Costos Extra

keywords configurables:
- "forrar" → +$500 por cuaderno
- "nombre" → +$200 por item
- "etiqueta" → +$300 por item

### 8.2 Storage

- AWS S3 bucket: utiles-platform-images
- Carpeta: /lists/{list_id}/image.jpg

---

## 9. Autenticación

- JWT tokens
- Refresh tokens
- Roles: USER, ADMIN, OPERATOR