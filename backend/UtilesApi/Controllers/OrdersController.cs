using Microsoft.AspNetCore.Mvc;
using UtilesApi.DTOs;
using UtilesApi.Infrastructure.Database;
using UtilesApi.Core.Entities;
using UtilesApi.Services;

namespace UtilesApi.Controllers;

[ApiController]
[Route("api/[controller]")]
public class OrdersController : ControllerBase
{
    private readonly OrderRepository _orderRepo;
    private readonly OrderItemRepository _orderItemRepo;
    private readonly ProductRepository _productRepo;
    private readonly AdditionalCostRepository _additionalCostRepo;
    private readonly INotificationService _notifications;

    public OrdersController(
        OrderRepository orderRepo,
        OrderItemRepository orderItemRepo,
        ProductRepository productRepo,
        AdditionalCostRepository additionalCostRepo,
        INotificationService notifications)
    {
        _orderRepo = orderRepo;
        _orderItemRepo = orderItemRepo;
        _productRepo = productRepo;
        _additionalCostRepo = additionalCostRepo;
        _notifications = notifications;
    }

    [HttpPost]
    public async Task<ActionResult<ApiResponse<OrderResponse>>> Create([FromBody] CreateOrderRequest request)
    {
        if (request.Items.Count == 0)
            return BadRequest(ApiResponse<OrderResponse>.Fail("EMPTY_ORDER", "La orden debe tener al menos un producto"));

        decimal total = 0;
        var orderItems = new List<OrderItem>();
        var lowStockProducts = new List<(string name, int stock)>();

        foreach (var item in request.Items)
        {
            var product = await _productRepo.GetById(item.ProductId);
            if (product == null)
                return BadRequest(ApiResponse<OrderResponse>.Fail("PRODUCT_NOT_FOUND", $"Producto {item.ProductId} no encontrado"));

            var itemTotal = product.BasePrice * item.Quantity;
            total += itemTotal;

            orderItems.Add(new OrderItem
            {
                Id = Guid.NewGuid(),
                ProductId = item.ProductId,
                Quantity = item.Quantity,
                UnitPrice = product.BasePrice,
                Notes = item.Notes
            });

            // Check stock after this order
            var remainingStock = product.Stock - item.Quantity;
            if (remainingStock <= 0)
                lowStockProducts.Add((product.Name, 0));
            else if (remainingStock <= 5)
                lowStockProducts.Add((product.Name, remainingStock));
        }

        var allNotes = string.Join(" ", request.Items.Where(i => !string.IsNullOrEmpty(i.Notes)).Select(i => i.Notes));
        var additionalCosts = await _additionalCostRepo.CalculateAdditionalCosts(allNotes);
        total += additionalCosts;

        var order = new Order
        {
            Id = Guid.NewGuid(),
            UserId = request.UserId,
            SupplyListId = request.SupplyListId,
            Total = total,
            Status = OrderStatus.RECIBIDO,
            ShippingAddress = request.ShippingAddress,
            ShippingPhone = request.ShippingPhone,
            TrackingNumber = $"TRK-{Guid.NewGuid().ToString()[..8].ToUpper()}",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };

        // Save contact notes if provided
        if (!string.IsNullOrEmpty(request.ContactNotes))
        {
            order.ShippingAddress += $"\n---OBSERVACIONES---\n{request.ContactNotes}";
        }

        await _orderRepo.Create(order);
        await _orderItemRepo.CreateBatch(order.Id, orderItems);

        await _orderItemRepo.CreateStatusHistory(new OrderStatusHistory
        {
            Id = Guid.NewGuid(),
            OrderId = order.Id,
            Status = OrderStatus.RECIBIDO.ToString(),
            Notes = request.ContactNotes,
            CreatedAt = DateTime.UtcNow
        });

        // Get user name for notification
        var userRepo = HttpContext.RequestServices.GetRequiredService<UserRepository>();
        var user = await userRepo.GetById(request.UserId);
        var customerName = user?.Name ?? "Cliente";

        // Fire notifications
        _ = Task.Run(async () =>
        {
            try
            {
                await _notifications.NotifyNewOrder(order.Id.ToString(), customerName, total);

                foreach (var (name, stock) in lowStockProducts)
                {
                    if (stock == 0)
                        await _notifications.NotifyOutOfStock(name);
                    else
                        await _notifications.NotifyLowStock(name, stock);
                }
            }
            catch { /* best effort */ }
        });

        var response = await BuildOrderResponse(order.Id);
        return Ok(ApiResponse<OrderResponse>.Ok(response));
    }

    [HttpGet("{id}")]
    public async Task<ActionResult<ApiResponse<OrderResponse>>> GetById(Guid id)
    {
        var order = await _orderRepo.GetById(id);
        if (order == null)
            return NotFound(ApiResponse<OrderResponse>.Fail("NOT_FOUND", "Orden no encontrada"));

        var response = await BuildOrderResponse(id);
        return Ok(ApiResponse<OrderResponse>.Ok(response));
    }

    [HttpGet("user/{userId}")]
    public async Task<ActionResult<ApiResponse<IEnumerable<OrderResponse>>>> GetByUserId(Guid userId)
    {
        var orders = await _orderRepo.GetByUserId(userId);
        var responses = new List<OrderResponse>();

        foreach (var order in orders)
        {
            responses.Add(await BuildOrderResponse(order.Id));
        }

        return Ok(ApiResponse<IEnumerable<OrderResponse>>.Ok(responses));
    }

    [HttpPut("{id}/status")]
    public async Task<ActionResult<ApiResponse<bool>>> UpdateStatus(Guid id, [FromBody] UpdateOrderStatusRequest request)
    {
        if (!Enum.TryParse<OrderStatus>(request.Status, out var status))
            return BadRequest(ApiResponse<bool>.Fail("INVALID_STATUS", "Estado invalido"));

        var order = await _orderRepo.GetById(id);
        if (order == null)
            return NotFound(ApiResponse<bool>.Fail("NOT_FOUND", "Orden no encontrada"));

        await _orderRepo.UpdateStatus(id, status);

        await _orderItemRepo.CreateStatusHistory(new OrderStatusHistory
        {
            Id = Guid.NewGuid(),
            OrderId = id,
            Status = status.ToString(),
            Notes = request.Notes,
            CreatedAt = DateTime.UtcNow
        });

        // Notify user of status change
        _ = Task.Run(async () =>
        {
            try { await _notifications.NotifyOrderStatusChange(order.UserId.ToString(), id.ToString(), status.ToString()); }
            catch { }
        });

        return Ok(ApiResponse<bool>.Ok(true));
    }

    // Admin: get all orders
    [HttpGet]
    public async Task<ActionResult<ApiResponse<IEnumerable<OrderResponse>>>> GetAll()
    {
        using var conn = HttpContext.RequestServices.GetRequiredService<IDbConnectionFactory>().CreateConnection();
        var orders = await Dapper.SqlMapper.QueryAsync<Order>(conn, "SELECT * FROM orders ORDER BY created_at DESC LIMIT 100");

        var responses = new List<OrderResponse>();
        foreach (var order in orders)
        {
            responses.Add(await BuildOrderResponse(order.Id));
        }

        return Ok(ApiResponse<IEnumerable<OrderResponse>>.Ok(responses));
    }

    // Admin: get dashboard stats
    [HttpGet("stats")]
    public async Task<ActionResult<ApiResponse<object>>> GetStats()
    {
        using var conn = HttpContext.RequestServices.GetRequiredService<IDbConnectionFactory>().CreateConnection();

        var totalOrders = await Dapper.SqlMapper.ExecuteScalarAsync<int>(conn, "SELECT COUNT(*) FROM orders");
        var totalRevenue = await Dapper.SqlMapper.ExecuteScalarAsync<decimal>(conn, "SELECT COALESCE(SUM(total), 0) FROM orders");
        var pendingOrders = await Dapper.SqlMapper.ExecuteScalarAsync<int>(conn, "SELECT COUNT(*) FROM orders WHERE status = 'RECIBIDO'");
        var totalProducts = await Dapper.SqlMapper.ExecuteScalarAsync<int>(conn, "SELECT COUNT(*) FROM products WHERE is_active = true");
        var lowStockCount = await Dapper.SqlMapper.ExecuteScalarAsync<int>(conn, "SELECT COUNT(*) FROM products WHERE is_active = true AND stock <= 5");
        var outOfStockCount = await Dapper.SqlMapper.ExecuteScalarAsync<int>(conn, "SELECT COUNT(*) FROM products WHERE is_active = true AND stock = 0");
        var totalLists = await Dapper.SqlMapper.ExecuteScalarAsync<int>(conn, "SELECT COUNT(*) FROM supply_lists");
        var pendingLists = await Dapper.SqlMapper.ExecuteScalarAsync<int>(conn, "SELECT COUNT(*) FROM supply_lists WHERE estado = 'PENDIENTE_REVISION'");
        var totalUsers = await Dapper.SqlMapper.ExecuteScalarAsync<int>(conn, "SELECT COUNT(*) FROM users");

        var lowStockProducts = await Dapper.SqlMapper.QueryAsync<dynamic>(conn,
            "SELECT name, stock, base_price as basePrice FROM products WHERE is_active = true AND stock <= 10 ORDER BY stock ASC LIMIT 20");

        return Ok(ApiResponse<object>.Ok(new
        {
            totalOrders,
            totalRevenue,
            pendingOrders,
            totalProducts,
            lowStockCount,
            outOfStockCount,
            totalLists,
            pendingLists,
            totalUsers,
            lowStockProducts
        }));
    }

    private async Task<OrderResponse> BuildOrderResponse(Guid orderId)
    {
        var order = await _orderRepo.GetById(orderId);
        var items = await _orderItemRepo.GetByOrderId(orderId);
        var history = await _orderItemRepo.GetStatusHistory(orderId);

        var itemResponses = new List<OrderItemResponse>();
        foreach (var item in items)
        {
            var product = await _productRepo.GetById(item.ProductId);
            itemResponses.Add(new OrderItemResponse
            {
                Id = item.Id,
                ProductId = item.ProductId,
                ProductName = product?.Name ?? "Producto desconocido",
                Quantity = item.Quantity,
                UnitPrice = item.UnitPrice,
                Notes = item.Notes
            });
        }

        return new OrderResponse
        {
            Id = order!.Id,
            UserId = order.UserId,
            SupplyListId = order.SupplyListId,
            Total = order.Total,
            Status = order.Status.ToString(),
            ShippingAddress = order.ShippingAddress,
            ShippingPhone = order.ShippingPhone,
            TrackingNumber = order.TrackingNumber,
            CreatedAt = order.CreatedAt,
            Items = itemResponses,
            StatusHistory = history.Select(h => new OrderStatusHistoryResponse
            {
                Status = h.Status,
                Notes = h.Notes,
                CreatedAt = h.CreatedAt
            }).ToList()
        };
    }
}

public class UpdateOrderStatusRequest
{
    public string Status { get; set; } = string.Empty;
    public string? Notes { get; set; }
}
