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

    public async Task<Product?> GetById(Guid id)
    {
        using var connection = _db.CreateConnection();
        return await connection.QueryFirstOrDefaultAsync<Product>(
            "SELECT * FROM products WHERE id = @Id", new { Id = id });
    }

    public async Task<IEnumerable<Product>> GetAll(string? category = null, string? brand = null, int limit = 100)
    {
        using var connection = _db.CreateConnection();
        var sql = "SELECT * FROM products WHERE is_active = true";
        var parameters = new DynamicParameters();

        if (!string.IsNullOrEmpty(category))
        {
            sql += " AND category = @Category";
            parameters.Add("Category", category);
        }
        if (!string.IsNullOrEmpty(brand))
        {
            sql += " AND brand = @Brand";
            parameters.Add("Brand", brand);
        }

        sql += " ORDER BY name LIMIT @Limit";
        parameters.Add("Limit", limit);

        return await connection.QueryAsync<Product>(sql, parameters);
    }

    public async Task<IEnumerable<Product>> Search(string query, string? category = null, int limit = 20)
    {
        using var connection = _db.CreateConnection();
        var sql = @"
            SELECT * FROM products 
            WHERE is_active = true 
            AND (LOWER(name) LIKE LOWER(@Query) OR LOWER(description) LIKE LOWER(@Query))";
        
        var parameters = new DynamicParameters();
        parameters.Add("Query", $"%{query}%");

        if (!string.IsNullOrEmpty(category))
        {
            sql += " AND category = @Category";
            parameters.Add("Category", category);
        }

        sql += " ORDER BY name LIMIT @Limit";
        parameters.Add("Limit", limit);

        return await connection.QueryAsync<Product>(sql, parameters);
    }

    public async Task<Guid> Create(Product product)
    {
        using var connection = _db.CreateConnection();
        var id = await connection.ExecuteScalarAsync<Guid>(@"
            INSERT INTO products (name, description, category, brand, sku, base_price, image_url, stock, attributes, is_active)
            VALUES (@Name, @Description, @Category, @Brand, @Sku, @BasePrice, @ImageUrl, @Stock, @Attributes::jsonb, @IsActive)
            RETURNING id", product);
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
                sku = @Sku,
                base_price = @BasePrice,
                image_url = @ImageUrl,
                stock = @Stock,
                attributes = @Attributes::jsonb,
                is_active = @IsActive,
                updated_at = NOW()
            WHERE id = @Id", product);
    }

    public async Task Delete(Guid id)
    {
        using var connection = _db.CreateConnection();
        await connection.ExecuteAsync("UPDATE products SET is_active = false WHERE id = @Id", new { Id = id });
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