using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Npgsql;
using UtilesApi.DTOs;
using UtilesApi.Infrastructure.Database;
using UtilesApi.Core.Entities;
using UtilesApi.Services;

namespace UtilesApi.Controllers;

[ApiController]
[Route("api/[controller]")]
public class ProductsController : ControllerBase
{
    private static readonly string[] Tiers = { "economico", "medio", "premium" };
    private readonly ProductRepository _productRepo;

    public ProductsController(ProductRepository productRepo)
    {
        _productRepo = productRepo;
    }

    /// <summary>
    /// Catálogo público (activos, sin Servicios salvo category=Servicios).
    /// includeInactive=true (solo ADMIN/OPERATOR) trae también inactivos y Servicios.
    /// </summary>
    [HttpGet]
    [AllowAnonymous]
    public async Task<ActionResult<ApiResponse<IEnumerable<ProductResponse>>>> GetAll(
        [FromQuery] string? category, [FromQuery] string? brand, [FromQuery] int limit = 100, [FromQuery] bool includeInactive = false)
    {
        if (includeInactive && !User.IsStaff())
            return User.GetUserId() == null
                ? Unauthorized(ApiResponse<IEnumerable<ProductResponse>>.Fail("UNAUTHORIZED", "Inicia sesión para continuar"))
                : StatusCode(403, ApiResponse<IEnumerable<ProductResponse>>.Fail("FORBIDDEN", "No tienes permiso para esta acción"));

        var products = await _productRepo.GetAll(category, brand, limit, includeInactive);
        return Ok(ApiResponse<IEnumerable<ProductResponse>>.Ok(products.Select(ProductResponse.From)));
    }

    [HttpGet("search")]
    [AllowAnonymous]
    public async Task<ActionResult<ApiResponse<IEnumerable<ProductResponse>>>> Search([FromQuery] string q, [FromQuery] string? category, [FromQuery] int limit = 20)
    {
        if (string.IsNullOrWhiteSpace(q))
            return BadRequest(ApiResponse<IEnumerable<ProductResponse>>.Fail("EMPTY_QUERY", "Debe ingresar una busqueda"));

        var products = await _productRepo.Search(q, category, limit);
        return Ok(ApiResponse<IEnumerable<ProductResponse>>.Ok(products.Select(ProductResponse.From)));
    }

    [HttpGet("categories")]
    [AllowAnonymous]
    public async Task<ActionResult<ApiResponse<IEnumerable<string>>>> GetCategories()
    {
        var categories = await _productRepo.GetCategories();
        return Ok(ApiResponse<IEnumerable<string>>.Ok(categories));
    }

    [HttpGet("{id}")]
    [AllowAnonymous]
    public async Task<ActionResult<ApiResponse<object>>> GetById(Guid id)
    {
        var product = await _productRepo.GetById(id);
        if (product == null)
            return NotFound(ApiResponse<object>.Fail("NOT_FOUND", "Producto no encontrado"));

        using var conn = HttpContext.RequestServices.GetRequiredService<IDbConnectionFactory>().CreateConnection();
        var variantTypes = await Dapper.SqlMapper.QueryAsync<dynamic>(conn, @"
            SELECT id, name, display_order FROM product_variant_types
            WHERE product_id = @Id ORDER BY display_order", new { Id = id });

        var variants = new List<object>();
        foreach (var vt in variantTypes)
        {
            var values = await Dapper.SqlMapper.QueryAsync<dynamic>(conn, @"
                SELECT id, value, image_url, price_modifier, stock, display_order, is_active, color_hex, description
                FROM product_variant_values WHERE variant_type_id = @TypeId ORDER BY display_order",
                new { TypeId = (Guid)vt.id });
            variants.Add(new { id = vt.id, name = vt.name, displayOrder = vt.display_order, values });
        }

        // Se mantienen las claves snake_case que ya lee la tienda y se agregan las camelCase del contrato
        var images = await Dapper.SqlMapper.QueryAsync<dynamic>(conn, @"
            SELECT id, image_url, alt_text, display_order, is_primary,
                   image_url AS ""imageUrl"", alt_text AS ""altText"", display_order AS ""displayOrder"", is_primary AS ""isPrimary""
            FROM product_images WHERE product_id = @Id ORDER BY is_primary DESC, display_order", new { Id = id });

        return Ok(ApiResponse<object>.Ok(new
        {
            product.Id, product.Name, product.Description, product.Category, product.Brand,
            product.Sku, product.BasePrice, product.ImageUrl, product.Stock, product.Attributes,
            product.Rating, product.Tier, product.SaleUnit, product.UnitQuantity,
            product.IsActive, product.SalePrice, product.CampaignSlug,
            variants, images
        }));
    }

    [HttpPost]
    [Authorize(Roles = AppRoles.Staff)]
    public async Task<ActionResult<ApiResponse<ProductResponse>>> Create([FromBody] ProductRequest request)
    {
        var error = Validar(request, creando: true);
        if (error != null) return BadRequest(ApiResponse<ProductResponse>.Fail("INVALID_PRODUCT", error));

        var product = new Product
        {
            Name = request.Name.Trim(),
            Description = request.Description,
            Category = request.Category.Trim(),
            Brand = string.IsNullOrWhiteSpace(request.Brand) ? null : request.Brand.Trim(),
            Sku = request.Sku?.Trim() ?? string.Empty,
            BasePrice = request.BasePrice,
            ImageUrl = request.ImageUrl,
            Stock = request.Stock,
            Attributes = string.IsNullOrWhiteSpace(request.Attributes) ? null : request.Attributes,
            Tier = request.Tier?.Trim().ToLowerInvariant() ?? "medio",
            IsActive = request.IsActive ?? true,
        };

        Guid id;
        try { id = await _productRepo.Create(product); }
        catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UniqueViolation)
        {
            return Conflict(ApiResponse<ProductResponse>.Fail("SKU_EXISTS", "Ya existe un producto con ese SKU"));
        }

        if (!string.IsNullOrWhiteSpace(product.ImageUrl))
            await SincronizarImagenPrincipal(id, product.ImageUrl);

        var creado = await _productRepo.GetById(id);
        return Ok(ApiResponse<ProductResponse>.Ok(ProductResponse.From(creado!)));
    }

    /// <summary>Actualiza el producto. ImageUrl, Attributes, Tier e IsActive en null = no cambian.</summary>
    [HttpPut("{id}")]
    [Authorize(Roles = AppRoles.Staff)]
    public async Task<ActionResult<ApiResponse<bool>>> Update(Guid id, [FromBody] ProductRequest request)
    {
        var product = await _productRepo.GetById(id);
        if (product == null)
            return NotFound(ApiResponse<bool>.Fail("NOT_FOUND", "Producto no encontrado"));

        var error = Validar(request, creando: false);
        if (error != null) return BadRequest(ApiResponse<bool>.Fail("INVALID_PRODUCT", error));

        var imagenAnterior = product.ImageUrl;
        product.Name = request.Name.Trim();
        product.Description = request.Description;
        product.Category = request.Category.Trim();
        product.Brand = string.IsNullOrWhiteSpace(request.Brand) ? null : request.Brand.Trim();
        product.Sku = request.Sku?.Trim() ?? string.Empty;
        product.BasePrice = request.BasePrice;
        product.Stock = request.Stock;
        if (request.ImageUrl != null) product.ImageUrl = request.ImageUrl == "" ? null : request.ImageUrl;
        if (request.Attributes != null) product.Attributes = request.Attributes == "" ? null : request.Attributes;
        if (request.Tier != null) product.Tier = request.Tier.Trim().ToLowerInvariant();
        if (request.IsActive.HasValue) product.IsActive = request.IsActive.Value;

        try { await _productRepo.Update(product); }
        catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UniqueViolation)
        {
            return Conflict(ApiResponse<bool>.Fail("SKU_EXISTS", "Ya existe un producto con ese SKU"));
        }

        if (!string.IsNullOrWhiteSpace(product.ImageUrl) && product.ImageUrl != imagenAnterior)
            await SincronizarImagenPrincipal(id, product.ImageUrl);

        return Ok(ApiResponse<bool>.Ok(true));
    }

    /// <summary>Desactiva el producto (no se borra: lo usan pedidos y listas). Se reactiva con PUT isActive=true.</summary>
    [HttpDelete("{id}")]
    [Authorize(Roles = AppRoles.Staff)]
    public async Task<ActionResult<ApiResponse<bool>>> Delete(Guid id)
    {
        await _productRepo.Delete(id);
        return Ok(ApiResponse<bool>.Ok(true));
    }

    /// <summary>Si la URL ya está en la galería la marca como principal; si no, la agrega como principal.</summary>
    private async Task SincronizarImagenPrincipal(Guid productId, string url)
    {
        using var conn = HttpContext.RequestServices.GetRequiredService<IDbConnectionFactory>().CreateConnection();
        await Dapper.SqlMapper.ExecuteAsync(conn, @"
            UPDATE product_images SET is_primary = (image_url = @Url) WHERE product_id = @Id;
            INSERT INTO product_images (product_id, image_url, is_primary, display_order)
            SELECT @Id, @Url, TRUE, COALESCE((SELECT MAX(display_order) + 1 FROM product_images WHERE product_id = @Id), 0)
            WHERE NOT EXISTS (SELECT 1 FROM product_images WHERE product_id = @Id AND image_url = @Url);",
            new { Id = productId, Url = url });
    }

    private static string? Validar(ProductRequest r, bool creando)
    {
        if (string.IsNullOrWhiteSpace(r.Name)) return "Escribe el nombre del producto";
        if (r.Name.Length > 200) return "El nombre puede tener hasta 200 caracteres";
        if (string.IsNullOrWhiteSpace(r.Category)) return "Elige una categoría";
        if (r.BasePrice < 0) return "El precio no puede ser negativo";
        if (r.BasePrice > 100000) return "El precio es demasiado alto";
        if (r.Stock < 0) return "El stock no puede ser negativo";
        if (!Validacion.Largo(r.Description, 5000)) return "La descripción puede tener hasta 5000 caracteres";
        if (!Validacion.Largo(r.Attributes, 20000)) return "Los atributos son demasiado largos";
        if (r.Tier != null && !Tiers.Contains(r.Tier.Trim().ToLowerInvariant()))
            return "El nivel debe ser economico, medio o premium";
        if (!string.IsNullOrWhiteSpace(r.Attributes))
        {
            try { using var _ = System.Text.Json.JsonDocument.Parse(r.Attributes); }
            catch { return "Los atributos deben ser JSON válido"; }
        }
        return null;
    }
}

public class ProductRequest
{
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
    public string Category { get; set; } = string.Empty;
    public string? Brand { get; set; }
    public string? Sku { get; set; }
    public decimal BasePrice { get; set; }
    /// <summary>null = no cambia (PUT); "" = quitar</summary>
    public string? ImageUrl { get; set; }
    public int Stock { get; set; }
    /// <summary>JSON en texto. null = no cambia (PUT)</summary>
    public string? Attributes { get; set; }
    /// <summary>economico, medio o premium. null = no cambia (PUT) / medio (POST)</summary>
    public string? Tier { get; set; }
    /// <summary>null = no cambia (PUT) / true (POST)</summary>
    public bool? IsActive { get; set; }
}
