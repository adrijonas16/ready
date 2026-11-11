using Microsoft.AspNetCore.SignalR;

namespace UtilesApi.Services;

public class NotificationHub : Hub
{
    // Solo ADMIN/OPERATOR reciben los avisos de la tienda (pedidos, stock, listas nuevas).
    // El token se envía con accessTokenFactory (llega como ?access_token=).
    public async Task JoinAdminGroup()
    {
        if (!Context.User.IsStaff()) return;
        await Groups.AddToGroupAsync(Context.ConnectionId, "admins");
    }

    // Cada usuario solo puede escuchar sus propios avisos.
    public async Task JoinUserGroup(string userId)
    {
        var propio = Context.User.GetUserId();
        if (propio == null || !string.Equals(propio.Value.ToString(), userId, StringComparison.OrdinalIgnoreCase)) return;
        await Groups.AddToGroupAsync(Context.ConnectionId, $"user-{propio.Value}");
    }
}

public interface INotificationService
{
    Task NotifyNewOrder(string orderId, string customerName, decimal total);
    Task NotifyLowStock(string productName, int currentStock);
    Task NotifyOutOfStock(string productName);
    Task NotifyNewList(string listId, string schoolName, string gradeName);
    Task NotifyNewMessage(string fromName, string message);
    Task NotifyOrderStatusChange(string userId, string orderId, string newStatus);
    Task NotifyListReady(string userId, string listId);
    Task NotifyNewInquiry(string inquiryId, string producto, string fromName);
    Task NotifyInquiryAnswered(string userId, string inquiryId, string producto, bool disponible);
}

public class NotificationService : INotificationService
{
    private readonly IHubContext<NotificationHub> _hub;

    public NotificationService(IHubContext<NotificationHub> hub)
    {
        _hub = hub;
    }

    public async Task NotifyNewOrder(string orderId, string customerName, decimal total)
    {
        await _hub.Clients.Group("admins").SendAsync("NewOrder", new
        {
            type = "new_order",
            orderId,
            customerName,
            total,
            message = $"Nuevo pedido de {customerName} por S/ {total:N2}",
            timestamp = DateTime.UtcNow
        });
    }

    public async Task NotifyLowStock(string productName, int currentStock)
    {
        await _hub.Clients.Group("admins").SendAsync("LowStock", new
        {
            type = "low_stock",
            productName,
            currentStock,
            message = $"Stock bajo: {productName} ({currentStock} restantes)",
            timestamp = DateTime.UtcNow
        });
    }

    public async Task NotifyOutOfStock(string productName)
    {
        await _hub.Clients.Group("admins").SendAsync("OutOfStock", new
        {
            type = "out_of_stock",
            productName,
            message = $"Sin stock: {productName}",
            timestamp = DateTime.UtcNow
        });
    }

    public async Task NotifyNewList(string listId, string schoolName, string gradeName)
    {
        await _hub.Clients.Group("admins").SendAsync("NewList", new
        {
            type = "new_list",
            listId,
            schoolName,
            gradeName,
            message = $"Nueva lista: {schoolName} - {gradeName}",
            timestamp = DateTime.UtcNow
        });
    }

    public async Task NotifyNewMessage(string fromName, string message)
    {
        await _hub.Clients.Group("admins").SendAsync("NewMessage", new
        {
            type = "new_message",
            fromName,
            message,
            timestamp = DateTime.UtcNow
        });
    }

    public async Task NotifyOrderStatusChange(string userId, string orderId, string newStatus)
    {
        await _hub.Clients.Group($"user-{userId}").SendAsync("OrderUpdate", new
        {
            type = "order_update",
            orderId,
            status = newStatus,
            message = $"Tu pedido #{orderId[..8]} cambio a: {newStatus}",
            timestamp = DateTime.UtcNow
        });
    }

    public async Task NotifyListReady(string userId, string listId)
    {
        await _hub.Clients.Group($"user-{userId}").SendAsync("ListReady", new
        {
            type = "list_ready",
            listId,
            message = "Tu lista de útiles fue aprobada. Ya puedes elegir tu opción y comprar.",
            timestamp = DateTime.UtcNow
        });
    }

    public async Task NotifyNewInquiry(string inquiryId, string producto, string fromName)
    {
        await _hub.Clients.Group("admins").SendAsync("NewInquiry", new
        {
            type = "new_inquiry",
            inquiryId,
            message = $"{fromName} pregunta si podemos conseguir: {producto}",
            timestamp = DateTime.UtcNow
        });
    }

    public async Task NotifyInquiryAnswered(string userId, string inquiryId, string producto, bool disponible)
    {
        await _hub.Clients.Group($"user-{userId}").SendAsync("InquiryAnswered", new
        {
            type = "inquiry_answered",
            inquiryId,
            disponible,
            message = disponible ? $"¡Sí conseguimos {producto}! Mira la respuesta en Mis consultas." : $"No pudimos conseguir {producto}. Mira la respuesta en Mis consultas.",
            timestamp = DateTime.UtcNow
        });
    }
}
