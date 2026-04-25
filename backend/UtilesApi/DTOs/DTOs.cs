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
}

public class CreateOrderRequest
{
    public Guid UserId { get; set; }
    public Guid? SupplyListId { get; set; }
    public List<OrderItemRequest> Items { get; set; } = new();
    public string ShippingAddress { get; set; } = string.Empty;
    public string ShippingPhone { get; set; } = string.Empty;
}

public class OrderItemRequest
{
    public Guid ProductId { get; set; }
    public int Quantity { get; set; }
    public string? Notes { get; set; }
}

public class OrderResponse
{
    public Guid Id { get; set; }
    public Guid UserId { get; set; }
    public Guid? SupplyListId { get; set; }
    public decimal Total { get; set; }
    public string Status { get; set; } = string.Empty;
    public string ShippingAddress { get; set; } = string.Empty;
    public string ShippingPhone { get; set; } = string.Empty;
    public string? TrackingNumber { get; set; }
    public DateTime CreatedAt { get; set; }
    public List<OrderItemResponse> Items { get; set; } = new();
    public List<OrderStatusHistoryResponse> StatusHistory { get; set; } = new();
}

public class OrderItemResponse
{
    public Guid Id { get; set; }
    public Guid ProductId { get; set; }
    public string ProductName { get; set; } = string.Empty;
    public int Quantity { get; set; }
    public decimal UnitPrice { get; set; }
    public string? Notes { get; set; }
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
}

public class UpdateSupplyItemRequest
{
    public Guid? ProductId { get; set; }
    public string? NombreDetectado { get; set; }
    public int? Cantidad { get; set; }
    public string? Notas { get; set; }
    public int? UserCustomQuantity { get; set; }
    public string? UserNotas { get; set; }
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
    public string Role { get; set; } = string.Empty;
}

public class AuthResponse
{
    public string Token { get; set; } = string.Empty;
    public UserResponse User { get; set; } = new();
}