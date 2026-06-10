using Microsoft.AspNetCore.Mvc;
using Dapper;
using UtilesApi.DTOs;
using UtilesApi.Infrastructure.Database;

namespace UtilesApi.Controllers;

[ApiController]
[Route("api/products/{productId}/variants")]
public class ProductVariantsController : ControllerBase
{
    private readonly IDbConnectionFactory _db;

    public ProductVariantsController(IDbConnectionFactory db)
    {
        _db = db;
    }

    // GET all variant types + values for a product
    [HttpGet]
    public async Task<IActionResult> GetVariants(Guid productId)
    {
        using var conn = _db.CreateConnection();

        var types = await conn.QueryAsync<dynamic>(@"
            SELECT id, name, display_order
            FROM product_variant_types
            WHERE product_id = @ProductId
            ORDER BY display_order", new { ProductId = productId });

        var result = new List<object>();
        foreach (var t in types)
        {
            var values = await conn.QueryAsync<dynamic>(@"
                SELECT id, value, image_url, price_modifier, stock, display_order, is_active, color_hex
                FROM product_variant_values
                WHERE variant_type_id = @TypeId
                ORDER BY display_order", new { TypeId = (Guid)t.id });

            result.Add(new { id = t.id, name = t.name, displayOrder = t.display_order, values });
        }

        return Ok(ApiResponse<object>.Ok(result));
    }

    // POST create variant type
    [HttpPost("types")]
    public async Task<IActionResult> CreateVariantType(Guid productId, [FromBody] CreateVariantTypeRequest req)
    {
        using var conn = _db.CreateConnection();
        var id = await conn.ExecuteScalarAsync<Guid>(@"
            INSERT INTO product_variant_types (product_id, name, display_order)
            VALUES (@ProductId, @Name, @DisplayOrder)
            RETURNING id", new { ProductId = productId, req.Name, req.DisplayOrder });

        return Ok(ApiResponse<Guid>.Ok(id));
    }

    // POST create variant value
    [HttpPost("types/{typeId}/values")]
    public async Task<IActionResult> CreateVariantValue(Guid productId, Guid typeId, [FromBody] CreateVariantValueRequest req)
    {
        using var conn = _db.CreateConnection();
        var id = await conn.ExecuteScalarAsync<Guid>(@"
            INSERT INTO product_variant_values (variant_type_id, value, image_url, price_modifier, stock, display_order, color_hex)
            VALUES (@TypeId, @Value, @ImageUrl, @PriceModifier, @Stock, @DisplayOrder, @ColorHex)
            RETURNING id", new { TypeId = typeId, req.Value, req.ImageUrl, req.PriceModifier, req.Stock, req.DisplayOrder, req.ColorHex });

        return Ok(ApiResponse<Guid>.Ok(id));
    }

    // PUT update variant value
    [HttpPut("values/{valueId}")]
    public async Task<IActionResult> UpdateVariantValue(Guid productId, Guid valueId, [FromBody] UpdateVariantValueRequest req)
    {
        using var conn = _db.CreateConnection();
        await conn.ExecuteAsync(@"
            UPDATE product_variant_values SET
                value = COALESCE(@Value, value),
                image_url = COALESCE(@ImageUrl, image_url),
                price_modifier = COALESCE(@PriceModifier, price_modifier),
                stock = COALESCE(@Stock, stock),
                is_active = COALESCE(@IsActive, is_active),
                color_hex = COALESCE(@ColorHex, color_hex)
            WHERE id = @Id", new { Id = valueId, req.Value, req.ImageUrl, req.PriceModifier, req.Stock, req.IsActive });

        return Ok(ApiResponse<bool>.Ok(true));
    }

    // DELETE variant type (cascades to values)
    [HttpDelete("types/{typeId}")]
    public async Task<IActionResult> DeleteVariantType(Guid productId, Guid typeId)
    {
        using var conn = _db.CreateConnection();
        await conn.ExecuteAsync("DELETE FROM product_variant_types WHERE id = @Id AND product_id = @ProductId",
            new { Id = typeId, ProductId = productId });
        return Ok(ApiResponse<bool>.Ok(true));
    }

    // DELETE variant value
    [HttpDelete("values/{valueId}")]
    public async Task<IActionResult> DeleteVariantValue(Guid productId, Guid valueId)
    {
        using var conn = _db.CreateConnection();
        await conn.ExecuteAsync("DELETE FROM product_variant_values WHERE id = @Id", new { Id = valueId });
        return Ok(ApiResponse<bool>.Ok(true));
    }

    // POST upload variant image
    [HttpPost("values/{valueId}/image")]
    public async Task<IActionResult> UploadVariantImage(Guid productId, Guid valueId, [FromForm] IFormFile file)
    {
        if (file == null) return BadRequest(ApiResponse<object>.Fail("NO_FILE", "Sube una imagen"));

        var storage = HttpContext.RequestServices.GetRequiredService<UtilesApi.Infrastructure.Storage.IStorageService>();
        var imageUrl = await storage.UploadFileAsync(file.OpenReadStream(), file.FileName, file.ContentType);

        using var conn = _db.CreateConnection();
        await conn.ExecuteAsync("UPDATE product_variant_values SET image_url = @Url WHERE id = @Id",
            new { Url = imageUrl, Id = valueId });

        return Ok(ApiResponse<string>.Ok(imageUrl));
    }

    // GET product images gallery
    [HttpGet("~/api/products/{productId}/images")]
    public async Task<IActionResult> GetImages(Guid productId)
    {
        using var conn = _db.CreateConnection();
        var images = await conn.QueryAsync<dynamic>(@"
            SELECT id, image_url, alt_text, display_order, is_primary
            FROM product_images WHERE product_id = @ProductId
            ORDER BY display_order", new { ProductId = productId });

        return Ok(ApiResponse<object>.Ok(images));
    }

    // POST upload product image
    [HttpPost("~/api/products/{productId}/images")]
    public async Task<IActionResult> UploadImage(Guid productId, [FromForm] IFormFile file, [FromForm] string? altText, [FromForm] bool isPrimary = false)
    {
        if (file == null) return BadRequest(ApiResponse<object>.Fail("NO_FILE", "Sube una imagen"));

        var storage = HttpContext.RequestServices.GetRequiredService<UtilesApi.Infrastructure.Storage.IStorageService>();
        var imageUrl = await storage.UploadFileAsync(file.OpenReadStream(), file.FileName, file.ContentType);

        using var conn = _db.CreateConnection();

        if (isPrimary)
        {
            await conn.ExecuteAsync("UPDATE product_images SET is_primary = false WHERE product_id = @Id", new { Id = productId });
            await conn.ExecuteAsync("UPDATE products SET image_url = @Url WHERE id = @Id", new { Url = imageUrl, Id = productId });
        }

        var id = await conn.ExecuteScalarAsync<Guid>(@"
            INSERT INTO product_images (product_id, image_url, alt_text, is_primary)
            VALUES (@ProductId, @ImageUrl, @AltText, @IsPrimary)
            RETURNING id", new { ProductId = productId, ImageUrl = imageUrl, AltText = altText, IsPrimary = isPrimary });

        return Ok(ApiResponse<object>.Ok(new { id, imageUrl }));
    }
}

public class CreateVariantTypeRequest
{
    public string Name { get; set; } = "";
    public int DisplayOrder { get; set; }
}

public class CreateVariantValueRequest
{
    public string Value { get; set; } = "";
    public string? ImageUrl { get; set; }
    public decimal PriceModifier { get; set; }
    public int Stock { get; set; }
    public int DisplayOrder { get; set; }
    public string? ColorHex { get; set; }
}

public class UpdateVariantValueRequest
{
    public string? Value { get; set; }
    public string? ImageUrl { get; set; }
    public decimal? PriceModifier { get; set; }
    public int? Stock { get; set; }
    public bool? IsActive { get; set; }
    public string? ColorHex { get; set; }
}
