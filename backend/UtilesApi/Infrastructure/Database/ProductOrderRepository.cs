using System.Data;
using Dapper;
using UtilesApi.Core.Entities;

namespace UtilesApi.Infrastructure.Database;

public class ProductRepository
{
    private readonly IDbConnectionFactory _db;

    public ProductRepository(IDbConnectionFactory db)
    {
        _db = db;
    }

    /// <summary>
    /// Productos con su precio de campaña (sale_price / campaign_slug). Cuenta una campaña si está activa y hoy
    /// cae entre start_date y end_date (null = sin límite). Precio: custom_price del producto en la campaña, si no
    /// custom_discount_percent, si no el % de la campaña, si no el descuento fijo. Si hay varias, gana la más barata.
    /// </summary>
    public const string SelectConPrecio = @"
        SELECT p.*,
               CASE WHEN camp.precio IS NOT NULL AND camp.precio < p.base_price THEN camp.precio END AS sale_price,
               camp.slug AS campaign_slug
        FROM products p
        LEFT JOIN LATERAL (
            SELECT c.slug,
                   GREATEST(0, ROUND(CASE
                       WHEN cp.custom_price IS NOT NULL THEN cp.custom_price
                       WHEN cp.custom_discount_percent IS NOT NULL THEN p.base_price * (1 - cp.custom_discount_percent / 100)
                       WHEN COALESCE(c.discount_percent, 0) > 0 THEN p.base_price * (1 - c.discount_percent / 100)
                       ELSE p.base_price - COALESCE(c.discount_fixed, 0)
                   END, 2)) AS precio
            FROM campaign_products cp
            JOIN campaigns c ON c.id = cp.campaign_id
            WHERE cp.product_id = p.id
              AND COALESCE(c.is_active, TRUE) = TRUE
              AND (c.start_date IS NULL OR c.start_date <= NOW())
              AND (c.end_date IS NULL OR c.end_date >= NOW())
            ORDER BY precio ASC
            LIMIT 1
        ) camp ON TRUE";

    public async Task<Product?> GetById(Guid id)
    {
        using var connection = _db.CreateConnection();
        return await connection.QueryFirstOrDefaultAsync<Product>(
            SelectConPrecio + " WHERE p.id = @Id", new { Id = id });
    }

    public async Task<IEnumerable<Product>> GetByIds(IEnumerable<Guid> ids)
    {
        var lista = ids.Distinct().ToArray();
        if (lista.Length == 0) return Enumerable.Empty<Product>();
        using var connection = _db.CreateConnection();
        return await connection.QueryAsync<Product>(SelectConPrecio + " WHERE p.id = ANY(@Ids)", new { Ids = lista });
    }

    /// <summary>
    /// Catálogo. Por defecto solo activos y sin la categoría Servicios (salvo que se pida esa categoría).
    /// includeInactive (admin) trae también inactivos y Servicios.
    /// </summary>
    public async Task<IEnumerable<Product>> GetAll(string? category = null, string? brand = null, int limit = 100, bool includeInactive = false)
    {
        using var connection = _db.CreateConnection();
        var sql = SelectConPrecio + " WHERE 1=1";
        var parameters = new DynamicParameters();

        if (!includeInactive)
        {
            sql += " AND p.is_active = true";
            if (!string.Equals(category, "Servicios", StringComparison.OrdinalIgnoreCase))
                sql += " AND p.category <> 'Servicios'";
        }
        if (!string.IsNullOrEmpty(category))
        {
            sql += " AND p.category = @Category";
            parameters.Add("Category", category);
        }
        if (!string.IsNullOrEmpty(brand))
        {
            sql += " AND p.brand = @Brand";
            parameters.Add("Brand", brand);
        }

        sql += " ORDER BY p.name LIMIT @Limit";
        parameters.Add("Limit", Math.Clamp(limit, 1, 2000));

        return await connection.QueryAsync<Product>(sql, parameters);
    }

    public async Task<IEnumerable<Product>> Search(string query, string? category = null, int limit = 20)
    {
        using var connection = _db.CreateConnection();
        var sql = SelectConPrecio + @"
            WHERE p.is_active = true
            AND (LOWER(p.name) LIKE LOWER(@Query) OR LOWER(p.description) LIKE LOWER(@Query))";

        var parameters = new DynamicParameters();
        parameters.Add("Query", $"%{query}%");

        if (!string.IsNullOrEmpty(category))
        {
            sql += " AND p.category = @Category";
            parameters.Add("Category", category);
        }

        sql += " ORDER BY p.name LIMIT @Limit";
        parameters.Add("Limit", Math.Clamp(limit, 1, 500));

        return await connection.QueryAsync<Product>(sql, parameters);
    }

    public async Task<Guid> Create(Product product)
    {
        using var connection = _db.CreateConnection();
        var id = await connection.ExecuteScalarAsync<Guid>(@"
            INSERT INTO products (name, description, category, brand, sku, base_price, image_url, stock, attributes, is_active, tier)
            VALUES (@Name, @Description, @Category, @Brand, NULLIF(@Sku, ''), @BasePrice, @ImageUrl, @Stock, @Attributes::jsonb, @IsActive, @Tier)
            RETURNING id", new
        {
            product.Name, product.Description, product.Category, product.Brand, product.Sku, product.BasePrice,
            product.ImageUrl, product.Stock, product.Attributes, product.IsActive, product.Tier
        });
        return id;
    }

    public async Task Update(Product product)
    {
        using var connection = _db.CreateConnection();
        await connection.ExecuteAsync(@"
            UPDATE products SET
                name = @Name,
                description = @Description,
                category = @Category,
                brand = @Brand,
                sku = NULLIF(@Sku, ''),
                base_price = @BasePrice,
                image_url = @ImageUrl,
                stock = @Stock,
                attributes = @Attributes::jsonb,
                is_active = @IsActive,
                tier = @Tier,
                updated_at = NOW()
            WHERE id = @Id", new
        {
            product.Id, product.Name, product.Description, product.Category, product.Brand, product.Sku, product.BasePrice,
            product.ImageUrl, product.Stock, product.Attributes, product.IsActive, product.Tier
        });
    }

    public async Task Delete(Guid id)
    {
        using var connection = _db.CreateConnection();
        await connection.ExecuteAsync("UPDATE products SET is_active = false, updated_at = NOW() WHERE id = @Id", new { Id = id });
    }

    public async Task<IEnumerable<string>> GetCategories()
    {
        using var connection = _db.CreateConnection();
        return await connection.QueryAsync<string>(
            "SELECT DISTINCT category FROM products WHERE is_active = true ORDER BY category");
    }
}

public class OrderRepository
{
    private readonly IDbConnectionFactory _db;

    public OrderRepository(IDbConnectionFactory db)
    {
        _db = db;
    }

    public async Task<Order?> GetById(Guid id)
    {
        using var connection = _db.CreateConnection();
        return await connection.QueryFirstOrDefaultAsync<Order>(
            "SELECT * FROM orders WHERE id = @Id", new { Id = id });
    }

    public async Task<IEnumerable<Order>> GetByUserId(Guid userId)
    {
        using var connection = _db.CreateConnection();
        return await connection.QueryAsync<Order>(
            "SELECT * FROM orders WHERE user_id = @UserId ORDER BY created_at DESC",
            new { UserId = userId });
    }

    public async Task<Guid> Create(Order order)
    {
        using var connection = _db.CreateConnection();
        var id = await connection.ExecuteScalarAsync<Guid>(@"
            INSERT INTO orders (user_id, supply_list_id, total, status, shipping_address, shipping_phone, tracking_number)
            VALUES (@UserId, @SupplyListId, @Total, @Status, @ShippingAddress, @ShippingPhone, @TrackingNumber)
            RETURNING id", order);
        return id;
    }

    public async Task Update(Order order)
    {
        using var connection = _db.CreateConnection();
        await connection.ExecuteAsync(@"
            UPDATE orders SET
                total = @Total,
                status = @Status,
                shipping_address = @ShippingAddress,
                shipping_phone = @ShippingPhone,
                tracking_number = @TrackingNumber,
                updated_at = NOW()
            WHERE id = @Id", order);
    }

    public async Task UpdateStatus(Guid id, OrderStatus status)
    {
        using var connection = _db.CreateConnection();
        await connection.ExecuteAsync(
            "UPDATE orders SET status = @Status, updated_at = NOW() WHERE id = @Id",
            new { Id = id, Status = status.ToString() });
    }
}

public class OrderItemRepository
{
    private readonly IDbConnectionFactory _db;

    public OrderItemRepository(IDbConnectionFactory db)
    {
        _db = db;
    }

    public async Task<IEnumerable<OrderItem>> GetByOrderId(Guid orderId)
    {
        using var connection = _db.CreateConnection();
        return await connection.QueryAsync<OrderItem>(
            "SELECT * FROM order_items WHERE order_id = @OrderId",
            new { OrderId = orderId });
    }

    public async Task CreateBatch(Guid orderId, IEnumerable<OrderItem> items)
    {
        using var connection = _db.CreateConnection();
        foreach (var item in items)
        {
            item.OrderId = orderId;
            await connection.ExecuteAsync(@"
                INSERT INTO order_items (order_id, product_id, quantity, unit_price, notes)
                VALUES (@OrderId, @ProductId, @Quantity, @UnitPrice, @Notes)", item);
        }
    }

    public async Task CreateStatusHistory(OrderStatusHistory history)
    {
        using var connection = _db.CreateConnection();
        await connection.ExecuteAsync(@"
            INSERT INTO order_status_history (order_id, status, notes, changed_by)
            VALUES (@OrderId, @Status, @Notes, @ChangedBy)", history);
    }

    public async Task<IEnumerable<OrderStatusHistory>> GetStatusHistory(Guid orderId)
    {
        using var connection = _db.CreateConnection();
        return await connection.QueryAsync<OrderStatusHistory>(
            "SELECT * FROM order_status_history WHERE order_id = @OrderId ORDER BY created_at DESC",
            new { OrderId = orderId });
    }
}

public class AdditionalCostRepository
{
    private readonly IDbConnectionFactory _db;

    public AdditionalCostRepository(IDbConnectionFactory db)
    {
        _db = db;
    }

    public async Task<IEnumerable<AdditionalCost>> GetActive()
    {
        using var connection = _db.CreateConnection();
        return await connection.QueryAsync<AdditionalCost>(
            "SELECT * FROM additional_costs WHERE is_active = true");
    }

    public async Task<decimal> CalculateAdditionalCosts(string? notes)
    {
        if (string.IsNullOrEmpty(notes)) return 0;

        var costs = await GetActive();
        decimal total = 0;
        foreach (var cost in costs)
        {
            if (notes.Contains(cost.Keyword, StringComparison.OrdinalIgnoreCase))
            {
                total += cost.Cost;
            }
        }
        return total;
    }
}