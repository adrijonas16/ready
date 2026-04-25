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

            CREATE INDEX IF NOT EXISTS idx_supply_lists_estado ON supply_lists(estado);
            CREATE INDEX IF NOT EXISTS idx_supply_lists_user_id ON supply_lists(user_id);
            CREATE INDEX IF NOT EXISTS idx_supply_items_list_id ON supply_items(supply_list_id);
            CREATE INDEX IF NOT EXISTS idx_orders_user_id ON orders(user_id);
            CREATE INDEX IF NOT EXISTS idx_products_category ON products(category);
        ";

        using var cmd = connection.CreateCommand();
        cmd.CommandText = createTablesSql;
        cmd.ExecuteNonQuery();

        SeedData(connection);
    }

    private void SeedData(IDbConnection connection)
    {
        using var checkCmd = connection.CreateCommand();
        checkCmd.CommandText = "SELECT COUNT(*) FROM products";
        var count = Convert.ToInt64(checkCmd.ExecuteScalar());
        if (count > 0) return;

        // Insert products first
        var insertProducts = @"
            INSERT INTO products (id, name, description, category, brand, sku, base_price, stock, image_url) VALUES
            (gen_random_uuid(), 'Cuaderno College 7mm 100 hojas', 'Cuaderno college 7mm con 100 hojas', 'Cuadernos', 'Colegio', 'CU001', 2990, 50, 'https://picsum.photos/seed/cuaderno/400/400'),
            (gen_random_uuid(), 'Cuaderno College 5mm 100 hojas', 'Cuaderno college 5mm con 100 hojas', 'Cuadernos', 'Colegio', 'CU002', 2890, 45, 'https://picsum.photos/seed/cuaderno5/400/400'),
            (gen_random_uuid(), 'Cuaderno Cuadriculado 7mm', 'Cuaderno cuadriculado 7mm', 'Cuadernos', 'Norma', 'CU003', 3490, 30, 'https://picsum.photos/seed/cuadriculado/400/400'),
            (gen_random_uuid(), 'Lapices de Colores 12 unidades', 'Set de 12 lapices de colores', 'Colores', 'Faber', 'LC001', 4990, 40, 'https://picsum.photos/seed/colores12/400/400'),
            (gen_random_uuid(), 'Lapices de Colores 24 unidades', 'Set de 24 lapices de colores', 'Colores', 'Faber', 'LC002', 7990, 25, 'https://picsum.photos/seed/colores24/400/400'),
            (gen_random_uuid(), 'Lapiz Grafito HB', 'Lapiz grafito HB para escritura', 'Lapices', 'Faber', 'LG001', 350, 100, 'https://picsum.photos/seed/lapiz/400/400'),
            (gen_random_uuid(), 'Goma de Borrar', 'Goma de borrar blanca', 'Accesorios', 'Faber', 'GM001', 590, 80, 'https://picsum.photos/seed/goma/400/400'),
            (gen_random_uuid(), 'Regla 30cm', 'Regla de 30 centimetros', 'Accesorios', 'Normal', 'RG001', 490, 60, 'https://picsum.photos/seed/regla/400/400'),
            (gen_random_uuid(), 'Pegamento en Barra 40g', 'Pegamento en barra 40 gramos', 'Pegamentos', 'Pritt', 'PG001', 1290, 70, 'https://picsum.photos/seed/pegamento/400/400'),
            (gen_random_uuid(), 'Tijeras Escolares', 'Tijeras para uso escolar', 'Accesorios', 'Maped', 'TJ001', 1990, 35, 'https://picsum.photos/seed/tijeras/400/400'),
            (gen_random_uuid(), 'Block de Dibujo A4', 'Block de dibujo tamano A4', 'Papeles', 'Canson', 'BL001', 2990, 40, 'https://picsum.photos/seed/block/400/400'),
            (gen_random_uuid(), 'Mochila Escolar', 'Mochila resistente para escuela', 'Mochilas', '中国家', 'MO001', 15990, 20, 'https://picsum.photos/seed/mochila/400/400');
        ";
        using var prodCmd = connection.CreateCommand();
        prodCmd.CommandText = insertProducts;
        prodCmd.ExecuteNonQuery();

        // Get product IDs for matching
        var prodCmd2 = connection.CreateCommand();
        prodCmd2.CommandText = "SELECT id, name FROM products WHERE sku IN ('CU001', 'LC001', 'GM001', 'RG001', 'PG001')";
        var reader = prodCmd2.ExecuteReader();
        var products = new Dictionary<string, string>();
        while (reader.Read())
        {
            products[reader.GetString(1)] = reader.GetGuid(0).ToString();
        }
        reader.Close();

        // Insert schools
        var insertSchools = @"
            INSERT INTO schools (id, name, address) VALUES
            (gen_random_uuid(), 'Colegio Santa Maria', 'Av. Providencia 1234, Santiago'),
            (gen_random_uuid(), 'Colegio San Patricio', 'Los Dominicos 567, Las Condes'),
            (gen_random_uuid(), 'Liceo Publico N1', 'Ahumada 100, Centro');
        ";
        using var schoolCmd = connection.CreateCommand();
        schoolCmd.CommandText = insertSchools;
        schoolCmd.ExecuteNonQuery();

        // Get school IDs
        var schoolCmd2 = connection.CreateCommand();
        schoolCmd2.CommandText = "SELECT id, name FROM schools";
        var schoolReader = schoolCmd2.ExecuteReader();
        var schools = new Dictionary<string, string>();
        while (schoolReader.Read())
        {
            schools[schoolReader.GetString(1)] = schoolReader.GetGuid(0).ToString();
        }
        schoolReader.Close();

        // Insert grades
        var santaMariaId = schools["Colegio Santa Maria"];
        var insertGrades = $@"
            INSERT INTO grades (id, school_id, name, year) VALUES
            (gen_random_uuid(), '{santaMariaId}', '1 Basico', 2026),
            (gen_random_uuid(), '{santaMariaId}', '2 Basico', 2026),
            (gen_random_uuid(), '{santaMariaId}', '3 Basico', 2026);
        ";
        using var gradeCmd = connection.CreateCommand();
        gradeCmd.CommandText = insertGrades;
        gradeCmd.ExecuteNonQuery();

        // Get grade ID
        var gradeCmd2 = connection.CreateCommand();
        gradeCmd2.CommandText = "SELECT id FROM grades WHERE name = '1 Basico'";
        var gradeId = gradeCmd2.ExecuteScalar().ToString();

        // Insert supply list
        var schoolId = santaMariaId;
        var insertList = $@"
            INSERT INTO supply_lists (id, school_id, grade_id, year, image_url, estado, es_oficial, fecha_subida, ocr_text) VALUES
            (gen_random_uuid(), '{schoolId}', '{gradeId}', 2026, 'https://picsum.photos/seed/lista1/800/1200', 'PROCESADA', true, CURRENT_TIMESTAMP, 'Lista 1 Basico 2026');
        ";
        using var listCmd = connection.CreateCommand();
        listCmd.CommandText = insertList;
        listCmd.ExecuteNonQuery();

        // Get list ID
        var listCmd2 = connection.CreateCommand();
        listCmd2.CommandText = "SELECT id FROM supply_lists WHERE estado = 'PROCESADA'";
        var listId = listCmd2.ExecuteScalar().ToString();

        // Insert supply items with product matching
        var cuadernoId = products["Cuaderno College 7mm 100 hojas"];
        var coloresId = products["Lapices de Colores 12 unidades"];
        var gomaId = products["Goma de Borrar"];
        var reglaId = products["Regla 30cm"];
        var pegamentoId = products["Pegamento en Barra 40g"];

        var insertItems = $@"
            INSERT INTO supply_items (id, supply_list_id, nombre_original, nombre_detectado, cantidad, notas, matched_product_id, matched_quantity, price_at_match) VALUES
            (gen_random_uuid(), '{listId}', 'Cuaderno college 7mm', 'Cuaderno College 7mm 100 hojas', 3, NULL, '{cuadernoId}', 3, 2990),
            (gen_random_uuid(), '{listId}', 'Lapices de colores 12', 'Lapices de Colores 12 unidades', 1, NULL, '{coloresId}', 1, 4990),
            (gen_random_uuid(), '{listId}', 'Goma de borrar', 'Goma de Borrar', 1, NULL, '{gomaId}', 1, 590),
            (gen_random_uuid(), '{listId}', 'Regla 30cm', 'Regla 30cm', 1, NULL, '{reglaId}', 1, 490),
            (gen_random_uuid(), '{listId}', 'Pegamento', 'Pegamento en Barra 40g', 1, 'Forrar cuadernos', '{pegamentoId}', 1, 1290);
        ";
        using var itemCmd = connection.CreateCommand();
        itemCmd.CommandText = insertItems;
        itemCmd.ExecuteNonQuery();
    }
}