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
    /// <summary>DNI o RUC para boleta/factura</summary>
    public string? Documento { get; set; }
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
    public string? UserObservaciones { get; set; }
    public string? Plan { get; set; }
    public string? EstudianteNombre { get; set; }
    public string? EstudianteGrado { get; set; }
    /// <summary>PROCESANDO, LISTO, ERROR o MANUAL (sin IA configurada).</summary>
    public string? IaEstado { get; set; }
    public string? IaMensaje { get; set; }
    /// <summary>JSON con forrado, etiquetas, carátulas y libros elegidos para la lista.</summary>
    public string? Extras { get; set; }
    /// <summary>Lista original de la que se copió (copia personal de una familia) o null.</summary>
    public Guid? CopiaDe { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}

public enum ListStatus
{
    PENDIENTE_REVISION,
    EN_REVISION,
    OBSERVADA,
    RECHAZADA,
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
    public bool Agregado { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    public int? UserCustomQuantity { get; set; }
    public string? UserNotas { get; set; }
    public bool Forro { get; set; }
    public string? ForroColor { get; set; }
    public string? Etiqueta { get; set; }
    public bool EtiquetaDibujo { get; set; }
    public bool Caratula { get; set; }
    public string? CaratulaCurso { get; set; }
    public string? DatosEstudiante { get; set; }
    public Guid? ProductEconomicoId { get; set; }
    public Guid? ProductMedioId { get; set; }
    public Guid? ProductPremiumId { get; set; }
    public decimal? PriceEconomico { get; set; }
    public decimal? PriceMedio { get; set; }
    public decimal? PricePremium { get; set; }
}

public class Section
{
    public Guid Id { get; set; }
    public string GroupName { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public int SortOrder { get; set; }
    public DateTime CreatedAt { get; set; }
}

public class Category
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; }
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
    public decimal Rating { get; set; } = 4.0m;
    public string Tier { get; set; } = "medio";
    public string SaleUnit { get; set; } = "unidad";
    public int UnitQuantity { get; set; } = 1;
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }

    // Calculados (no son columnas): precio con la campaña activa que incluye al producto
    public decimal? SalePrice { get; set; }
    public string? CampaignSlug { get; set; }

    /// <summary>Precio que se cobra hoy: el de campaña si hay, si no el base.</summary>
    public decimal PrecioVigente => SalePrice ?? BasePrice;
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
    public string? ContactNotes { get; set; }
    /// <summary>JSON: { metodo, operacion, estado }</summary>
    public string? Pago { get; set; }
    /// <summary>JSON: { fecha, franja }</summary>
    public string? Entrega { get; set; }
    /// <summary>JSON: { tipo, documento, razonSocial }</summary>
    public string? Comprobante { get; set; }
    public decimal CostoEnvio { get; set; }
    public Guid[]? SupplyListIds { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}

public enum OrderStatus
{
    RECIBIDO,
    EN_PREPARACION,
    ARMADO,
    EN_CAMINO,
    ENTREGADO,
    CANCELADO
}

public class OrderItem
{
    public Guid Id { get; set; }
    public Guid OrderId { get; set; }
    public Guid ProductId { get; set; }
    public int Quantity { get; set; }
    public decimal UnitPrice { get; set; }
    public string? Notes { get; set; }
    /// <summary>Para quién es (nombre del hijo/alumno)</summary>
    public string? Para { get; set; }
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