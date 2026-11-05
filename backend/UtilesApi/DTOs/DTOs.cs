namespace UtilesApi.DTOs;

public class ApiResponse
{
    public bool Success { get; set; }
    public object? Data { get; set; }
    public ApiError? Error { get; set; }

    public static ApiResponse Fail(string code, string message) => new()
    {
        Success = false,
        Error = new ApiError { Code = code, Message = message }
    };
}

public class ApiResponse<T>
{
    public bool Success { get; set; }
    public T? Data { get; set; }
    public ApiError? Error { get; set; }

    public static ApiResponse<T> Ok(T data) => new() { Success = true, Data = data };
    public static ApiResponse<T> Fail(string code, string message) => new()
    {
        Success = false,
        Error = new ApiError { Code = code, Message = message }
    };
}

public class ApiError
{
    public string Code { get; set; } = string.Empty;
    public string Message { get; set; } = string.Empty;
}

public class ListResponse
{
    public Guid Id { get; set; }
    public Guid? UserId { get; set; }
    public Guid SchoolId { get; set; }
    public string SchoolName { get; set; } = string.Empty;
    public Guid GradeId { get; set; }
    public string GradeName { get; set; } = string.Empty;
    public int Year { get; set; }
    public string? ImageUrl { get; set; }
    public string? OcrText { get; set; }
    public string Estado { get; set; } = string.Empty;
    public bool EsOficial { get; set; }
    public string? Observaciones { get; set; }
    public string? SubmittedBy { get; set; }
    public string? UserObservaciones { get; set; }
    public string? Plan { get; set; }
    public string? EstudianteNombre { get; set; }
    public string? EstudianteGrado { get; set; }
    public string? IaEstado { get; set; }
    public string? IaMensaje { get; set; }
    public string? Extras { get; set; }
    /// <summary>Id de la lista original si es una copia personal; null si es original.</summary>
    public Guid? CopiaDe { get; set; }
    public DateTime? FechaSubida { get; set; }
    public DateTime? FechaInicioRevision { get; set; }
    public DateTime? FechaValidacion { get; set; }
    public DateTime CreatedAt { get; set; }
}

public class SupplyItemResponse
{
    public Guid Id { get; set; }
    public Guid SupplyListId { get; set; }
    public string NombreOriginal { get; set; } = string.Empty;
    public string? NombreDetectado { get; set; }
    public int Cantidad { get; set; }
    public string? Notas { get; set; }
    public Guid? MatchedProductId { get; set; }
    public ProductResponse? MatchedProduct { get; set; }
    public int? MatchedQuantity { get; set; }
    public decimal? PriceAtMatch { get; set; }
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
    public ProductResponse? ProductEconomico { get; set; }
    public decimal? PriceEconomico { get; set; }
    public Guid? ProductMedioId { get; set; }
    public ProductResponse? ProductMedio { get; set; }
    public decimal? PriceMedio { get; set; }
    public Guid? ProductPremiumId { get; set; }
    public ProductResponse? ProductPremium { get; set; }
    public decimal? PricePremium { get; set; }
    /// <summary>true si el cliente lo agregó por su cuenta (no viene en la lista del colegio)</summary>
    public bool Agregado { get; set; }
}

public class ListDetailResponse
{
    public ListResponse List { get; set; } = new();
    public List<SupplyItemResponse> Items { get; set; } = new();
}

public class ProductResponse
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
    public decimal Rating { get; set; }
    public string Tier { get; set; } = "medio";
    public bool IsActive { get; set; } = true;
    /// <summary>Precio con la campaña activa que incluye al producto, o null.</summary>
    public decimal? SalePrice { get; set; }
    public string? CampaignSlug { get; set; }
    public string SaleUnit { get; set; } = "unidad";
    public int UnitQuantity { get; set; } = 1;

    public static ProductResponse From(UtilesApi.Core.Entities.Product p) => new()
    {
        Id = p.Id,
        Name = p.Name,
        Description = p.Description,
        Category = p.Category,
        Brand = p.Brand,
        Sku = p.Sku,
        BasePrice = p.BasePrice,
        ImageUrl = p.ImageUrl,
        Stock = p.Stock,
        Attributes = p.Attributes,
        Rating = p.Rating,
        Tier = p.Tier,
        IsActive = p.IsActive,
        SalePrice = p.SalePrice,
        CampaignSlug = p.CampaignSlug,
        SaleUnit = p.SaleUnit,
        UnitQuantity = p.UnitQuantity
    };
}

public class CreateOrderRequest
{
    /// <summary>Se ignora: el pedido siempre es del usuario del token.</summary>
    public Guid? UserId { get; set; }
    /// <summary>Compatibilidad: una sola lista. Se une con SupplyListIds.</summary>
    public Guid? SupplyListId { get; set; }
    public List<Guid>? SupplyListIds { get; set; }
    public List<OrderItemRequest> Items { get; set; } = new();
    public string ShippingAddress { get; set; } = string.Empty;
    public string ShippingPhone { get; set; } = string.Empty;
    public string? ContactNotes { get; set; }
    public PagoDto? Pago { get; set; }
    public EntregaDto? Entrega { get; set; }
    /// <summary>Lo que el cliente vio; el servidor lo recalcula con la configuración de la tienda.</summary>
    public decimal? CostoEnvio { get; set; }
    public ComprobanteDto? Comprobante { get; set; }
}

public class OrderItemRequest
{
    public Guid ProductId { get; set; }
    public int Quantity { get; set; }
    /// <summary>Solo sirve para validar el recargo de variante; el precio lo pone el servidor.</summary>
    public decimal? UnitPrice { get; set; }
    public string? Notes { get; set; }
    /// <summary>Para quién es (nombre del hijo)</summary>
    public string? Para { get; set; }
}

public class PagoDto
{
    public string? Metodo { get; set; }
    public string? Operacion { get; set; }
    public string? Estado { get; set; }
}

public class EntregaDto
{
    public string? Fecha { get; set; }
    public string? Franja { get; set; }
}

public class ComprobanteDto
{
    /// <summary>boleta o factura</summary>
    public string Tipo { get; set; } = "boleta";
    public string? Documento { get; set; }
    public string? RazonSocial { get; set; }
}

public class OrderResponse
{
    public Guid Id { get; set; }
    public Guid UserId { get; set; }
    public string? UserName { get; set; }
    public string? UserEmail { get; set; }
    public Guid? SupplyListId { get; set; }
    public List<Guid> SupplyListIds { get; set; } = new();
    public decimal Subtotal { get; set; }
    public decimal CostoEnvio { get; set; }
    public decimal Total { get; set; }
    public string Status { get; set; } = string.Empty;
    public string ShippingAddress { get; set; } = string.Empty;
    public string ShippingPhone { get; set; } = string.Empty;
    public string? ContactNotes { get; set; }
    public string? TrackingNumber { get; set; }
    public PagoDto? Pago { get; set; }
    public EntregaDto? Entrega { get; set; }
    public ComprobanteDto? Comprobante { get; set; }
    public DateTime CreatedAt { get; set; }
    public List<OrderItemResponse> Items { get; set; } = new();
    public List<OrderStatusHistoryResponse> StatusHistory { get; set; } = new();
}

public class OrderItemResponse
{
    public Guid Id { get; set; }
    public Guid ProductId { get; set; }
    public string ProductName { get; set; } = string.Empty;
    public string? ImageUrl { get; set; }
    public int Quantity { get; set; }
    public decimal UnitPrice { get; set; }
    public string? Notes { get; set; }
    public string? Para { get; set; }
}

public class OrderStatusHistoryResponse
{
    public string Status { get; set; } = string.Empty;
    public string? Notes { get; set; }
    public DateTime CreatedAt { get; set; }
}

public class SchoolResponse
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? Address { get; set; }
}

public class GradeResponse
{
    public Guid Id { get; set; }
    public Guid SchoolId { get; set; }
    public string Name { get; set; } = string.Empty;
    public int Year { get; set; }
}

public class UpdateListStatusRequest
{
    public string Status { get; set; } = string.Empty;
    public string? Observaciones { get; set; }
    /// <summary>null = no cambia</summary>
    public bool? EsOficial { get; set; }
}

public class UpdateListRequest
{
    public Guid? SchoolId { get; set; }
    public Guid? GradeId { get; set; }
    public int? Year { get; set; }
}

public class StoreSettingsDto
{
    public decimal CostoEnvio { get; set; }
    public decimal? EnvioGratisDesde { get; set; }
    public string Whatsapp { get; set; } = string.Empty;
    public string Telefono { get; set; } = string.Empty;
    public string HorarioEntrega { get; set; } = string.Empty;
    public int DiasEntrega { get; set; }
    public string? Zonas { get; set; }
}

public class UpdateProfileRequest
{
    public string? Name { get; set; }
    public string? Phone { get; set; }
    public string? Address { get; set; }
    public string? Documento { get; set; }
}

public class UpdateSupplyItemRequest
{
    public Guid? ProductId { get; set; }
    public string? NombreDetectado { get; set; }
    public int? Cantidad { get; set; }
    public string? Notas { get; set; }
    public int? UserCustomQuantity { get; set; }
    public string? UserNotas { get; set; }
    public bool? Forro { get; set; }
    public string? ForroColor { get; set; }
    public string? Etiqueta { get; set; }
    public bool? EtiquetaDibujo { get; set; }
    public bool? Caratula { get; set; }
    public string? CaratulaCurso { get; set; }
    public string? DatosEstudiante { get; set; }
    public string? NombreOriginal { get; set; }
    public Guid? ProductEconomicoId { get; set; }
    public Guid? ProductMedioId { get; set; }
    public Guid? ProductPremiumId { get; set; }
}

public class UpdateListPlanRequest
{
    public string Plan { get; set; } = "medio";
    public string? EstudianteNombre { get; set; }
    public string? EstudianteGrado { get; set; }
    /// <summary>
    /// true: cambia todos los útiles al nivel elegido. false: solo guarda los datos.
    /// Si no se envía, se cambian solo cuando el plan es distinto al guardado.
    /// </summary>
    public bool? AplicarATodos { get; set; }
}

public class UserRegisterRequest
{
    public string Email { get; set; } = string.Empty;
    public string Password { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
}

public class UserLoginRequest
{
    public string Email { get; set; } = string.Empty;
    public string Password { get; set; } = string.Empty;
}

public class UserResponse
{
    public Guid Id { get; set; }
    public string Email { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string? Phone { get; set; }
    public string? Address { get; set; }
    public string? Documento { get; set; }
    public string Role { get; set; } = string.Empty;

    public static UserResponse From(UtilesApi.Core.Entities.User u) => new()
    {
        Id = u.Id,
        Email = u.Email,
        Name = u.Name,
        Phone = u.Phone,
        Address = u.Address,
        Documento = u.Documento,
        Role = u.Role.ToString()
    };
}

public class AuthResponse
{
    public string Token { get; set; } = string.Empty;
    public UserResponse User { get; set; } = new();
}
