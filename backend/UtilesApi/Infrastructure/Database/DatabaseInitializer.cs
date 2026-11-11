using System.Data;

namespace UtilesApi.Infrastructure.Database;

public class DatabaseInitializer
{
    private readonly IDbConnectionFactory _db;

    public DatabaseInitializer(IDbConnectionFactory db)
    {
        _db = db;
    }

    public void Initialize()
    {
        using var connection = _db.CreateConnection();
        connection.Open();

        var createTablesSql = @"
            CREATE TABLE IF NOT EXISTS users (
                id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
                email VARCHAR(255) UNIQUE NOT NULL,
                password_hash VARCHAR(255) NOT NULL,
                name VARCHAR(255) NOT NULL,
                phone VARCHAR(50),
                address TEXT,
                role VARCHAR(20) DEFAULT 'USER',
                created_at TIMESTAMP DEFAULT CURRENT_TIMESTAMP,
                updated_at TIMESTAMP DEFAULT CURRENT_TIMESTAMP
            );

            CREATE TABLE IF NOT EXISTS schools (
                id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
                name VARCHAR(255) NOT NULL,
                address TEXT,
                created_at TIMESTAMP DEFAULT CURRENT_TIMESTAMP
            );

            CREATE TABLE IF NOT EXISTS grades (
                id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
                school_id UUID REFERENCES schools(id),
                name VARCHAR(100) NOT NULL,
                year INTEGER NOT NULL,
                created_at TIMESTAMP DEFAULT CURRENT_TIMESTAMP
            );

            CREATE TABLE IF NOT EXISTS supply_lists (
                id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
                user_id UUID REFERENCES users(id),
                school_id UUID REFERENCES schools(id),
                grade_id UUID REFERENCES grades(id),
                year INTEGER NOT NULL,
                image_url TEXT,
                ocr_text TEXT,
                parsed_college VARCHAR(255),
                parsed_grade VARCHAR(100),
                estado VARCHAR(50) DEFAULT 'PENDIENTE_REVISION',
                es_oficial BOOLEAN DEFAULT FALSE,
                observaciones TEXT,
                submitted_by VARCHAR(255),
                fecha_subida TIMESTAMP,
                fecha_inicio_revision TIMESTAMP,
                fecha_validacion TIMESTAMP,
                created_at TIMESTAMP DEFAULT CURRENT_TIMESTAMP,
                updated_at TIMESTAMP DEFAULT CURRENT_TIMESTAMP
            );

            CREATE TABLE IF NOT EXISTS supply_items (
                id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
                supply_list_id UUID REFERENCES supply_lists(id) ON DELETE CASCADE,
                product_id UUID,
                nombre_original VARCHAR(500) NOT NULL,
                nombre_detectado VARCHAR(500),
                cantidad INTEGER DEFAULT 1,
                notas TEXT,
                matched_product_id UUID,
                matched_quantity INTEGER,
                price_at_match DECIMAL(10,2),
                created_at TIMESTAMP DEFAULT CURRENT_TIMESTAMP,
                updated_at TIMESTAMP DEFAULT CURRENT_TIMESTAMP
            );

            DO $$ BEGIN
                ALTER TABLE supply_items ADD COLUMN IF NOT EXISTS product_economico_id UUID;
                ALTER TABLE supply_items ADD COLUMN IF NOT EXISTS product_medio_id UUID;
                ALTER TABLE supply_items ADD COLUMN IF NOT EXISTS product_premium_id UUID;
                ALTER TABLE supply_items ADD COLUMN IF NOT EXISTS price_economico DECIMAL(10,2);
                ALTER TABLE supply_items ADD COLUMN IF NOT EXISTS price_medio DECIMAL(10,2);
                ALTER TABLE supply_items ADD COLUMN IF NOT EXISTS price_premium DECIMAL(10,2);
            EXCEPTION WHEN duplicate_column THEN NULL;
            END $$;

            CREATE TABLE IF NOT EXISTS products (
                id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
                name VARCHAR(255) NOT NULL,
                description TEXT,
                category VARCHAR(100) NOT NULL,
                brand VARCHAR(100),
                sku VARCHAR(100) UNIQUE,
                base_price DECIMAL(10,2) NOT NULL,
                image_url TEXT,
                stock INTEGER DEFAULT 0,
                attributes JSONB,
                is_active BOOLEAN DEFAULT TRUE,
                created_at TIMESTAMP DEFAULT CURRENT_TIMESTAMP,
                updated_at TIMESTAMP DEFAULT CURRENT_TIMESTAMP
            );

            CREATE TABLE IF NOT EXISTS additional_costs (
                id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
                keyword VARCHAR(100) UNIQUE NOT NULL,
                description TEXT NOT NULL,
                cost DECIMAL(10,2) NOT NULL,
                is_active BOOLEAN DEFAULT TRUE
            );

            CREATE TABLE IF NOT EXISTS orders (
                id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
                user_id UUID REFERENCES users(id),
                supply_list_id UUID REFERENCES supply_lists(id),
                total DECIMAL(10,2) NOT NULL,
                status VARCHAR(50) DEFAULT 'RECIBIDO',
                shipping_address TEXT NOT NULL,
                shipping_phone VARCHAR(50) NOT NULL,
                tracking_number VARCHAR(100),
                created_at TIMESTAMP DEFAULT CURRENT_TIMESTAMP,
                updated_at TIMESTAMP DEFAULT CURRENT_TIMESTAMP
            );

            CREATE TABLE IF NOT EXISTS order_items (
                id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
                order_id UUID REFERENCES orders(id) ON DELETE CASCADE,
                product_id UUID REFERENCES products(id),
                quantity INTEGER NOT NULL,
                unit_price DECIMAL(10,2) NOT NULL,
                notes TEXT,
                created_at TIMESTAMP DEFAULT CURRENT_TIMESTAMP
            );

            CREATE TABLE IF NOT EXISTS order_status_history (
                id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
                order_id UUID REFERENCES orders(id) ON DELETE CASCADE,
                status VARCHAR(50) NOT NULL,
                notes TEXT,
                changed_by UUID REFERENCES users(id),
                created_at TIMESTAMP DEFAULT CURRENT_TIMESTAMP
            );

            CREATE TABLE IF NOT EXISTS sections (
                id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
                group_name VARCHAR(50) NOT NULL,
                name VARCHAR(100) NOT NULL,
                sort_order INTEGER NOT NULL DEFAULT 0,
                created_at TIMESTAMP DEFAULT CURRENT_TIMESTAMP
            );

            CREATE TABLE IF NOT EXISTS categories (
                id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
                name VARCHAR(100) UNIQUE NOT NULL,
                created_at TIMESTAMP DEFAULT CURRENT_TIMESTAMP
            );

            CREATE TABLE IF NOT EXISTS school_sections (
                school_id UUID REFERENCES schools(id) ON DELETE CASCADE,
                section_id UUID REFERENCES sections(id) ON DELETE CASCADE,
                PRIMARY KEY (school_id, section_id)
            );

            CREATE INDEX IF NOT EXISTS idx_supply_lists_estado ON supply_lists(estado);
            CREATE INDEX IF NOT EXISTS idx_supply_lists_user_id ON supply_lists(user_id);
            CREATE INDEX IF NOT EXISTS idx_supply_items_list_id ON supply_items(supply_list_id);
            CREATE INDEX IF NOT EXISTS idx_orders_user_id ON orders(user_id);
            CREATE INDEX IF NOT EXISTS idx_products_category ON products(category);

            -- Columnas de personalización y lectura con IA (idempotente: no toca columnas existentes)
            ALTER TABLE supply_lists ADD COLUMN IF NOT EXISTS user_observaciones TEXT;
            ALTER TABLE supply_lists ADD COLUMN IF NOT EXISTS plan VARCHAR(20);
            ALTER TABLE supply_lists ADD COLUMN IF NOT EXISTS estudiante_nombre VARCHAR(255);
            ALTER TABLE supply_lists ADD COLUMN IF NOT EXISTS estudiante_grado VARCHAR(100);
            ALTER TABLE supply_lists ADD COLUMN IF NOT EXISTS ia_estado VARCHAR(20);
            ALTER TABLE supply_lists ADD COLUMN IF NOT EXISTS ia_mensaje TEXT;
            ALTER TABLE supply_items ADD COLUMN IF NOT EXISTS user_custom_quantity INTEGER;
            ALTER TABLE supply_items ADD COLUMN IF NOT EXISTS user_notas TEXT;
            ALTER TABLE supply_items ADD COLUMN IF NOT EXISTS forro BOOLEAN DEFAULT FALSE;
            ALTER TABLE supply_items ADD COLUMN IF NOT EXISTS forro_color VARCHAR(50);
            ALTER TABLE supply_items ADD COLUMN IF NOT EXISTS etiqueta VARCHAR(100);
            ALTER TABLE supply_items ADD COLUMN IF NOT EXISTS etiqueta_dibujo BOOLEAN DEFAULT FALSE;
            ALTER TABLE supply_items ADD COLUMN IF NOT EXISTS caratula BOOLEAN DEFAULT FALSE;
            ALTER TABLE supply_items ADD COLUMN IF NOT EXISTS caratula_curso VARCHAR(100);
            ALTER TABLE supply_items ADD COLUMN IF NOT EXISTS datos_estudiante TEXT;
            ALTER TABLE products ADD COLUMN IF NOT EXISTS tier VARCHAR(20) DEFAULT 'medio';
            ALTER TABLE products ADD COLUMN IF NOT EXISTS rating DECIMAL(3,1) DEFAULT 4.0;
            ALTER TABLE products ADD COLUMN IF NOT EXISTS sale_unit VARCHAR(30) DEFAULT 'unidad';
            ALTER TABLE products ADD COLUMN IF NOT EXISTS unit_quantity INTEGER DEFAULT 1;
            ALTER TABLE supply_lists ADD COLUMN IF NOT EXISTS extras TEXT;
            -- Copia personal de una lista oficial: cada familia ajusta la suya sin cambiar la del colegio
            ALTER TABLE supply_lists ADD COLUMN IF NOT EXISTS copia_de UUID;
            -- Útiles que el cliente agregó por su cuenta (no vienen en la lista del colegio)
            ALTER TABLE supply_items ADD COLUMN IF NOT EXISTS agregado BOOLEAN DEFAULT FALSE;
            -- La usa el admin al crear productos y no estaba en la tabla
            ALTER TABLE products ADD COLUMN IF NOT EXISTS attributes JSONB;

            -- Servicios (forrado, etiquetas, carátulas) como productos para que pasen por carrito y pedido.
            -- Los precios son iniciales: se editan en Admin > Productos (categoría Servicios).
            INSERT INTO categories (name) SELECT 'Servicios' WHERE NOT EXISTS (SELECT 1 FROM categories WHERE name = 'Servicios');
            INSERT INTO products (name, description, category, brand, sku, base_price, stock, is_active, tier)
            SELECT v.name, v.description, 'Servicios', 'Ready', v.sku, v.price, 99999, TRUE, 'medio'
            FROM (VALUES
                ('Forrado de cuaderno', 'Forramos cada cuaderno con papel del color elegido y vinifán transparente.', 'SRV-FORRO-CUADERNO', 2.00),
                ('Forrado de libro', 'Forramos cada libro con vinifán transparente.', 'SRV-FORRO-LIBRO', 3.00),
                ('Etiqueta personalizada', 'Etiqueta con nombre, grado y diseño elegido, pegada en cada cuaderno y libro.', 'SRV-ETIQUETA', 0.50),
                ('Carátula personalizada', 'Carátula impresa por curso con el nombre del estudiante.', 'SRV-CARATULA', 2.50)
            ) AS v(name, description, sku, price)
            WHERE NOT EXISTS (SELECT 1 FROM products p WHERE p.sku = v.sku);

            -- Tablas que usan marcas, campañas, noticias y fotos de productos.
            -- Antes se creaban a mano; aquí se crean solas para que una base nueva funcione completa.
            CREATE TABLE IF NOT EXISTS brands (
                id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
                name VARCHAR(150) NOT NULL UNIQUE,
                logo_url TEXT,
                is_active BOOLEAN DEFAULT TRUE,
                created_at TIMESTAMPTZ DEFAULT NOW()
            );
            INSERT INTO brands (name)
            SELECT DISTINCT brand FROM products WHERE brand IS NOT NULL AND brand <> ''
            ON CONFLICT (name) DO NOTHING;

            CREATE TABLE IF NOT EXISTS campaigns (
                id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
                name VARCHAR(200) NOT NULL,
                slug VARCHAR(200) NOT NULL UNIQUE,
                description TEXT,
                image_url TEXT,
                discount_percent DECIMAL(5,2) DEFAULT 0,
                discount_fixed DECIMAL(10,2) DEFAULT 0,
                start_date TIMESTAMPTZ,
                end_date TIMESTAMPTZ,
                is_active BOOLEAN DEFAULT TRUE,
                created_at TIMESTAMPTZ DEFAULT NOW()
            );

            CREATE TABLE IF NOT EXISTS campaign_products (
                campaign_id UUID REFERENCES campaigns(id) ON DELETE CASCADE,
                product_id UUID REFERENCES products(id) ON DELETE CASCADE,
                custom_discount_percent DECIMAL(5,2),
                custom_price DECIMAL(10,2),
                PRIMARY KEY (campaign_id, product_id)
            );

            CREATE TABLE IF NOT EXISTS news (
                id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
                title VARCHAR(250) NOT NULL,
                content TEXT,
                image_url TEXT,
                link_url TEXT,
                campaign_id UUID REFERENCES campaigns(id) ON DELETE SET NULL,
                display_order INTEGER DEFAULT 0,
                is_active BOOLEAN DEFAULT TRUE,
                created_at TIMESTAMPTZ DEFAULT NOW()
            );

            CREATE TABLE IF NOT EXISTS product_images (
                id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
                product_id UUID REFERENCES products(id) ON DELETE CASCADE,
                image_url TEXT NOT NULL,
                alt_text TEXT,
                display_order INTEGER DEFAULT 0,
                is_primary BOOLEAN DEFAULT FALSE,
                created_at TIMESTAMPTZ DEFAULT NOW()
            );

            -- Variantes de productos (ya las usa el admin). Se crean si no existen.
            CREATE TABLE IF NOT EXISTS product_variant_types (
                id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
                product_id UUID REFERENCES products(id) ON DELETE CASCADE,
                name VARCHAR(100) NOT NULL,
                display_order INTEGER DEFAULT 0
            );
            CREATE TABLE IF NOT EXISTS product_variant_values (
                id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
                variant_type_id UUID REFERENCES product_variant_types(id) ON DELETE CASCADE,
                value VARCHAR(150) NOT NULL,
                image_url TEXT,
                price_modifier DECIMAL(10,2) DEFAULT 0,
                stock INTEGER DEFAULT 0,
                display_order INTEGER DEFAULT 0,
                is_active BOOLEAN DEFAULT TRUE,
                color_hex VARCHAR(20)
            );

            -- Opciones de los servicios, editables en Admin > Productos > (servicio) > Variantes:
            -- colores de forro, diseños de etiqueta (se les puede subir foto) y cursos para carátulas.
            INSERT INTO product_variant_types (product_id, name, display_order)
            SELECT p.id, v.tipo, 0
            FROM (VALUES ('SRV-FORRO-CUADERNO', 'Color'), ('SRV-ETIQUETA', 'Diseño'), ('SRV-CARATULA', 'Curso')) AS v(sku, tipo)
            JOIN products p ON p.sku = v.sku
            WHERE NOT EXISTS (SELECT 1 FROM product_variant_types t WHERE t.product_id = p.id AND t.name = v.tipo);

            INSERT INTO product_variant_values (variant_type_id, value, color_hex, stock, display_order)
            SELECT t.id, v.valor, v.hex, 99999, v.orden
            FROM (VALUES
                ('SRV-FORRO-CUADERNO', 'Color', 'Azul', '#2E62D9', 1), ('SRV-FORRO-CUADERNO', 'Color', 'Rojo', '#D93A2B', 2),
                ('SRV-FORRO-CUADERNO', 'Color', 'Verde', '#2F9E5B', 3), ('SRV-FORRO-CUADERNO', 'Color', 'Amarillo', '#F2C230', 4),
                ('SRV-FORRO-CUADERNO', 'Color', 'Rosado', '#F27BAA', 5), ('SRV-FORRO-CUADERNO', 'Color', 'Morado', '#7D55D8', 6),
                ('SRV-FORRO-CUADERNO', 'Color', 'Celeste', '#59B7E8', 7), ('SRV-FORRO-CUADERNO', 'Color', 'Naranja', '#F28B30', 8),
                ('SRV-FORRO-CUADERNO', 'Color', 'Solo vinifán', NULL, 9),
                ('SRV-ETIQUETA', 'Diseño', 'Lisa', NULL, 1), ('SRV-ETIQUETA', 'Diseño', 'Estrellas', NULL, 2),
                ('SRV-ETIQUETA', 'Diseño', 'Flores', NULL, 3), ('SRV-ETIQUETA', 'Diseño', 'Espacio', NULL, 4),
                ('SRV-ETIQUETA', 'Diseño', 'Deportes', NULL, 5), ('SRV-ETIQUETA', 'Diseño', 'Corazones', NULL, 6),
                ('SRV-CARATULA', 'Curso', 'Matemática', NULL, 1), ('SRV-CARATULA', 'Curso', 'Comunicación', NULL, 2),
                ('SRV-CARATULA', 'Curso', 'Ciencia y Tecnología', NULL, 3), ('SRV-CARATULA', 'Curso', 'Personal Social', NULL, 4),
                ('SRV-CARATULA', 'Curso', 'Inglés', NULL, 5), ('SRV-CARATULA', 'Curso', 'Arte y Cultura', NULL, 6),
                ('SRV-CARATULA', 'Curso', 'Educación Religiosa', NULL, 7), ('SRV-CARATULA', 'Curso', 'Educación Física', NULL, 8),
                ('SRV-CARATULA', 'Curso', 'Tutoría', NULL, 9)
            ) AS v(sku, tipo, valor, hex, orden)
            JOIN products p ON p.sku = v.sku
            JOIN product_variant_types t ON t.product_id = p.id AND t.name = v.tipo
            WHERE NOT EXISTS (SELECT 1 FROM product_variant_values x WHERE x.variant_type_id = t.id);

            -- Perfil del cliente: DNI/RUC para boleta o factura
            ALTER TABLE users ADD COLUMN IF NOT EXISTS documento VARCHAR(20);

            -- Pedido completo: notas de contacto, pago, entrega, comprobante, envío y varias listas (varios hijos)
            ALTER TABLE orders ADD COLUMN IF NOT EXISTS contact_notes TEXT;
            ALTER TABLE orders ADD COLUMN IF NOT EXISTS pago JSONB;
            ALTER TABLE orders ADD COLUMN IF NOT EXISTS entrega JSONB;
            ALTER TABLE orders ADD COLUMN IF NOT EXISTS comprobante JSONB;
            ALTER TABLE orders ADD COLUMN IF NOT EXISTS costo_envio DECIMAL(10,2) DEFAULT 0;
            ALTER TABLE orders ADD COLUMN IF NOT EXISTS supply_list_ids UUID[];
            ALTER TABLE order_items ADD COLUMN IF NOT EXISTS para VARCHAR(150);
            CREATE INDEX IF NOT EXISTS idx_orders_created_at ON orders(created_at);
            CREATE INDEX IF NOT EXISTS idx_orders_status ON orders(status);
            CREATE INDEX IF NOT EXISTS idx_order_items_order_id ON order_items(order_id);
            CREATE INDEX IF NOT EXISTS idx_campaign_products_product ON campaign_products(product_id);
            CREATE INDEX IF NOT EXISTS idx_supply_lists_copia_de ON supply_lists(copia_de);

            -- Pedidos antiguos guardaban las notas al final de la dirección: se pasan a contact_notes
            UPDATE orders SET
                contact_notes = substr(shipping_address, strpos(shipping_address, E'\n---OBSERVACIONES---\n') + 21),
                shipping_address = substr(shipping_address, 1, strpos(shipping_address, E'\n---OBSERVACIONES---\n') - 1)
            WHERE contact_notes IS NULL AND strpos(shipping_address, E'\n---OBSERVACIONES---\n') > 0;

            -- Versiones anteriores guardaban el número del enum en vez del nombre (0 = USER, 1 = ADMIN...)
            UPDATE users SET role = CASE role WHEN '0' THEN 'USER' WHEN '1' THEN 'ADMIN' WHEN '2' THEN 'OPERATOR' END
            WHERE role IN ('0', '1', '2');
            UPDATE orders SET status = CASE status WHEN '0' THEN 'RECIBIDO' WHEN '1' THEN 'EN_PREPARACION' WHEN '2' THEN 'ARMADO'
                                                   WHEN '3' THEN 'EN_CAMINO' WHEN '4' THEN 'ENTREGADO' WHEN '5' THEN 'CANCELADO' END
            WHERE status IN ('0', '1', '2', '3', '4', '5');

            -- Datos del padrón del MINEDU para los colegios (código modular, gestión y ubicación)
            ALTER TABLE schools ADD COLUMN IF NOT EXISTS codigo_modular VARCHAR(10);
            ALTER TABLE schools ADD COLUMN IF NOT EXISTS codigos_modulares TEXT;
            ALTER TABLE schools ADD COLUMN IF NOT EXISTS gestion VARCHAR(80);
            ALTER TABLE schools ADD COLUMN IF NOT EXISTS latitud DOUBLE PRECISION;
            ALTER TABLE schools ADD COLUMN IF NOT EXISTS longitud DOUBLE PRECISION;
            CREATE UNIQUE INDEX IF NOT EXISTS ux_schools_codigo_modular ON schools(codigo_modular) WHERE codigo_modular IS NOT NULL;
            CREATE INDEX IF NOT EXISTS idx_grades_school_year ON grades(school_id, year);

            -- Cargas de datos que se hacen una sola vez (para no volver a crear lo que el admin borró)
            CREATE TABLE IF NOT EXISTS seed_runs (
                name VARCHAR(100) PRIMARY KEY,
                ran_at TIMESTAMPTZ NOT NULL DEFAULT NOW()
            );

            -- Descripción propia de cada opción de un producto (p. ej. qué trae el diseño, medidas del color)
            ALTER TABLE product_variant_values ADD COLUMN IF NOT EXISTS description TEXT;

            -- Consultas de producto: el cliente pregunta por algo que no está en la tienda y se le responde en 24 h
            -- estado: PENDIENTE, DISPONIBLE (sí lo conseguimos) o NO_DISPONIBLE
            CREATE TABLE IF NOT EXISTS product_inquiries (
                id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
                user_id UUID REFERENCES users(id) ON DELETE SET NULL,
                supply_list_id UUID REFERENCES supply_lists(id) ON DELETE SET NULL,
                producto VARCHAR(200) NOT NULL,
                detalle VARCHAR(1000),
                nombre VARCHAR(120),
                contacto VARCHAR(150),
                estado VARCHAR(20) NOT NULL DEFAULT 'PENDIENTE',
                respuesta VARCHAR(1000),
                respondida_por UUID REFERENCES users(id) ON DELETE SET NULL,
                created_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
                respondida_at TIMESTAMPTZ
            );
            CREATE INDEX IF NOT EXISTS idx_product_inquiries_estado ON product_inquiries(estado, created_at);
            CREATE INDEX IF NOT EXISTS idx_product_inquiries_user ON product_inquiries(user_id);

            -- Configuración de la tienda (una sola fila). Se edita en Admin > Configuración (PUT /api/settings).
            CREATE TABLE IF NOT EXISTS store_settings (
                id INTEGER PRIMARY KEY DEFAULT 1 CHECK (id = 1),
                data JSONB NOT NULL,
                updated_at TIMESTAMPTZ DEFAULT NOW()
            );
            INSERT INTO store_settings (id, data) VALUES (1,
                '{""costoEnvio"": 8, ""envioGratisDesde"": 150, ""whatsapp"": ""51951237582"", ""telefono"": ""951 237 582"", ""horarioEntrega"": ""Lunes a sábado, 9:00 a 18:00"", ""diasEntrega"": 2}'::jsonb)
            ON CONFLICT (id) DO NOTHING;

            -- El número de ejemplo se reemplaza por el WhatsApp real de la tienda (solo si nadie lo cambió todavía)
            UPDATE store_settings
            SET data = data || '{""whatsapp"": ""51951237582"", ""telefono"": ""951 237 582""}'::jsonb, updated_at = NOW()
            WHERE id = 1 AND data->>'whatsapp' = '51959000111';
        ";

        using var cmd = connection.CreateCommand();
        cmd.CommandText = createTablesSql;
        cmd.ExecuteNonQuery();

        SeedData(connection);
    }

    private void SeedData(IDbConnection connection)
    {
        // Sections seed - always run independently
        using var sectionsCheck = connection.CreateCommand();
        sectionsCheck.CommandText = "SELECT COUNT(*) FROM sections";
        var sectionsCount = Convert.ToInt64(sectionsCheck.ExecuteScalar());
        if (sectionsCount == 0)
        {
            var insertSections = @"
                INSERT INTO sections (group_name, name, sort_order) VALUES
                ('Inicial', 'Inicial - 4 años', 1),
                ('Inicial', 'Inicial - 5 años', 2),
                ('Primaria', '1ro Primaria', 3),
                ('Primaria', '2do Primaria', 4),
                ('Primaria', '3ro Primaria', 5),
                ('Primaria', '4to Primaria', 6),
                ('Primaria', '5to Primaria', 7),
                ('Primaria', '6to Primaria', 8),
                ('Secundaria', '1ro Secundaria', 9),
                ('Secundaria', '2do Secundaria', 10),
                ('Secundaria', '3ro Secundaria', 11),
                ('Secundaria', '4to Secundaria', 12),
                ('Secundaria', '5to Secundaria', 13);
            ";
            using var secCmd = connection.CreateCommand();
            secCmd.CommandText = insertSections;
            secCmd.ExecuteNonQuery();
        }

        // Inicial de 3 años (los colegios del padrón la tienen); en bases anteriores no existía
        using (var tres = connection.CreateCommand())
        {
            tres.CommandText = @"
                INSERT INTO sections (group_name, name, sort_order)
                SELECT 'Inicial', 'Inicial - 3 años', 0
                WHERE NOT EXISTS (SELECT 1 FROM sections WHERE name = 'Inicial - 3 años')";
            tres.ExecuteNonQuery();
        }

        // Catálogo, colegios y listas de ejemplo (Arequipa, en soles). Solo si aún no hay productos fuera de los servicios.
        // El SQL se genera desde ready-web/src/demo/catalogo.ts, así la base nueva coincide con la tienda de demostración.
        using var checkCmd = connection.CreateCommand();
        checkCmd.CommandText = "SELECT COUNT(*) FROM products WHERE category <> 'Servicios'";
        var count = Convert.ToInt64(checkCmd.ExecuteScalar());
        if (count == 0)
        {
            using var stream = typeof(DatabaseInitializer).Assembly.GetManifestResourceStream("seed-catalogo.sql")
                ?? throw new InvalidOperationException("No se encontró seed-catalogo.sql dentro del ensamblado");
            using var lector = new StreamReader(stream);
            using var seedCmd = connection.CreateCommand();
            seedCmd.CommandText = lector.ReadToEnd();
            seedCmd.ExecuteNonQuery();
        }

        // Colegios del distrito de Arequipa con sus grados (padrón MINEDU). Una sola vez por base, y después
        // del catálogo de ejemplo (que asocia todos los niveles a sus 4 colegios de muestra, no a estos).
        CorrerUnaVez(connection, "colegios-arequipa-2026", "seed-colegios-arequipa.sql");
    }

    /// <summary>
    /// Corre un SQL embebido una sola vez (queda anotado en seed_runs), dentro de una transacción:
    /// si falla, no queda nada a medias y se vuelve a intentar en el próximo arranque.
    /// </summary>
    private static void CorrerUnaVez(IDbConnection connection, string nombre, string recurso)
    {
        using (var ya = connection.CreateCommand())
        {
            ya.CommandText = "SELECT COUNT(*) FROM seed_runs WHERE name = @n";
            var p = ya.CreateParameter(); p.ParameterName = "n"; p.Value = nombre; ya.Parameters.Add(p);
            if (Convert.ToInt64(ya.ExecuteScalar()) > 0) return;
        }

        using var stream = typeof(DatabaseInitializer).Assembly.GetManifestResourceStream(recurso)
            ?? throw new InvalidOperationException($"No se encontró {recurso} dentro del ensamblado");
        using var lector = new StreamReader(stream);
        using var tx = connection.BeginTransaction();
        using (var cmd = connection.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = lector.ReadToEnd();
            cmd.ExecuteNonQuery();
        }
        using (var marca = connection.CreateCommand())
        {
            marca.Transaction = tx;
            marca.CommandText = "INSERT INTO seed_runs (name) VALUES (@n)";
            var p = marca.CreateParameter(); p.ParameterName = "n"; p.Value = nombre; marca.Parameters.Add(p);
            marca.ExecuteNonQuery();
        }
        tx.Commit();
    }
}