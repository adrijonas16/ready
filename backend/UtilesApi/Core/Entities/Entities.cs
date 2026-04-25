using System;

namespace UtilesApi.Core.Entities;

public class User
{
    public Guid Id { get; set; }
    public string Email { get; set; } = string.Empty;
    public string PasswordHash { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string? Phone { get; set; }
    public string? Address { get; set; }
    public UserRole Role { get; set; } = UserRole.USER;
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}

public enum UserRole
{
    USER,
    ADMIN,
    OPERATOR
}

public class School
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? Address { get; set; }
    public DateTime CreatedAt { get; set; }
}

public class Grade
{
    public Guid Id { get; set; }
    public Guid SchoolId { get; set; }
    public string Name { get; set; } = string.Empty;
    public int Year { get; set; }
    public DateTime CreatedAt { get; set; }
}

public class SupplyList
{
    public Guid Id { get; set; }
    public Guid? UserId { get; set; }
    public Guid SchoolId { get; set; }
    public Guid GradeId { get; set; }
    public int Year { get; set; }
    public string? ImageUrl { get; set; }
    public string? OcrText { get; set; }
    public string? ParsedCollege { get; set; }
    public string? ParsedGrade { get; set; }
    public ListStatus Estado { get; set; } = ListStatus.PENDIENTE_REVISION;
    public bool EsOficial { get; set; }
    public string? Observaciones { get; set; }
    public string? SubmittedBy { get; set; }
    public DateTime? FechaSubida { get; set; }
    public DateTime? FechaInicioRevision { get; set; }
    public DateTime? FechaValidacion { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}

public enum ListStatus
{
    PENDIENTE_REVISION,
    EN_REVISION,
    OBSERVADA,
    VALIDADA,
    PROCESADA
}

public class SupplyItem
{
    public Guid Id { get; set; }
    public Guid SupplyListId { get; set; }
    public Guid? ProductId { get; set; }
    public string NombreOriginal { get; set; } = string.Empty;
    public string? NombreDetectado { get; set; }
    public int Cantidad { get; set; } = 1;
    public string? Notas { get; set; }
    public Guid? MatchedProductId { get; set; }
    public int? MatchedQuantity { get; set; }
    public decimal? PriceAtMatch { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    public int? UserCustomQuantity { get; set; }
    public string? UserNotas { get; set; }
}

public class Product
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
    public string Category { get; set; } = string.Empty;
    public string? Brand { get; set; }
    public string Sku { get; set; } = string.Empty;
    public decimal BasePrice { get; set; }
    public string? ImageUrl { get; set; }
    public int Stock { get; set; }
    public string? Attributes { get; set; }
    public bool IsActive { get; set; } = true;
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}

public class AdditionalCost
{
    public Guid Id { get; set; }
    public string Keyword { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public decimal Cost { get; set; }
    public bool IsActive { get; set; } = true;
}

public class Order
{
    public Guid Id { get; set; }
    public Guid UserId { get; set; }
    public Guid? SupplyListId { get; set; }
    public decimal Total { get; set; }
    public OrderStatus Status { get; set; } = OrderStatus.RECIBIDO;
    public string ShippingAddress { get; set; } = string.Empty;
    public string ShippingPhone { get; set; } = string.Empty;
    public string? TrackingNumber { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}

public enum OrderStatus
{
    RECIBIDO,
    EN_PREPARACION,
    ARMADO,
    EN_CAMINO,
    ENTREGADO
}

public class OrderItem
{
    public Guid Id { get; set; }
    public Guid OrderId { get; set; }
    public Guid ProductId { get; set; }
    public int Quantity { get; set; }
    public decimal UnitPrice { get; set; }
    public string? Notes { get; set; }
    public DateTime CreatedAt { get; set; }
}

public class OrderStatusHistory
{
    public Guid Id { get; set; }
    public Guid OrderId { get; set; }
    public string Status { get; set; } = string.Empty;
    public string? Notes { get; set; }
    public Guid? ChangedBy { get; set; }
    public DateTime CreatedAt { get; set; }
}