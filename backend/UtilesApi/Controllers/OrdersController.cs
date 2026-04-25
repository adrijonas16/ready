using Microsoft.AspNetCore.Mvc;
using UtilesApi.DTOs;
using UtilesApi.Infrastructure.Database;
using UtilesApi.Core.Entities;

namespace UtilesApi.Controllers;

[ApiController]
[Route("api/[controller]")]
public class OrdersController : ControllerBase
{
    private readonly OrderRepository _orderRepo;
    private readonly OrderItemRepository _orderItemRepo;
    private readonly ProductRepository _productRepo;
    private readonly AdditionalCostRepository _additionalCostRepo;

    public OrdersController(
        OrderRepository orderRepo,
        OrderItemRepository orderItemRepo,
        ProductRepository productRepo,
        AdditionalCostRepository additionalCostRepo)
    {
        _orderRepo = orderRepo;
        _orderItemRepo = orderItemRepo;
        _productRepo = productRepo;
        _additionalCostRepo = additionalCostRepo;
    }

    [HttpPost]
    public async Task<ActionResult<ApiResponse<OrderResponse>>> Create([FromBody] CreateOrderRequest request)
    {
        if (request.Items.Count == 0)
            return BadRequest(ApiResponse<OrderResponse>.Fail("EMPTY_ORDER", "La orden debe tener al menos un producto"));

        decimal total = 0;
        var orderItems = new List<OrderItem>();

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

        await _orderRepo.Create(order);
        await _orderItemRepo.CreateBatch(order.Id, orderItems);

        await _orderItemRepo.CreateStatusHistory(new OrderStatusHistory
        {
            Id = Guid.NewGuid(),
            OrderId = order.Id,
            Status = OrderStatus.RECIBIDO.ToString(),
            CreatedAt = DateTime.UtcNow
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

        await _orderRepo.UpdateStatus(id, status);

        await _orderItemRepo.CreateStatusHistory(new OrderStatusHistory
        {
            Id = Guid.NewGuid(),
            OrderId = id,
            Status = status.ToString(),
            Notes = request.Notes,
            CreatedAt = DateTime.UtcNow
        });

        return Ok(ApiResponse<bool>.Ok(true));
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