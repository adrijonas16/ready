using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Dapper;
using UtilesApi.DTOs;
using UtilesApi.Infrastructure.Security;
using UtilesApi.Infrastructure.Database;
using UtilesApi.Services;

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
    [AllowAnonymous]
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
    [Authorize(Roles = AppRoles.Staff)]
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
    [Authorize(Roles = AppRoles.Staff)]
    public async Task<IActionResult> CreateVariantValue(Guid productId, Guid typeId, [FromBody] CreateVariantValueRequest req)
    {
        if (string.IsNullOrWhiteSpace(req.Value) || req.Value.Length > 100)
            return BadRequest(ApiResponse<Guid>.Fail("INVALID", "Escribe la opción (hasta 100 caracteres)"));
        if (req.Stock < 0) return BadRequest(ApiResponse<Guid>.Fail("INVALID", "El stock no puede ser negativo"));
        if (req.PriceModifier is < -10000 or > 10000) return BadRequest(ApiResponse<Guid>.Fail("INVALID", "El recargo está fuera de rango"));
        using var conn = _db.CreateConnection();
        var tipoDelProducto = await conn.ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM product_variant_types WHERE id = @TypeId AND product_id = @ProductId", new { TypeId = typeId, ProductId = productId });
        if (tipoDelProducto == 0) return NotFound(ApiResponse<Guid>.Fail("NOT_FOUND", "Tipo de variante no encontrado"));
        var id = await conn.ExecuteScalarAsync<Guid>(@"
            INSERT INTO product_variant_values (variant_type_id, value, image_url, price_modifier, stock, display_order, color_hex)
            VALUES (@TypeId, @Value, @ImageUrl, @PriceModifier, @Stock, @DisplayOrder, @ColorHex)
            RETURNING id", new { TypeId = typeId, req.Value, req.ImageUrl, req.PriceModifier, req.Stock, req.DisplayOrder, req.ColorHex });

        return Ok(ApiResponse<Guid>.Ok(id));
    }

    // PUT update variant value
    // PUT update variant value: {value, colorHex, priceModifier, stock, isActive, imageUrl}; null = no cambia, "" = quitar (colorHex/imageUrl)
    [HttpPut("values/{valueId}")]
    [Authorize(Roles = AppRoles.Staff)]
    public async Task<IActionResult> UpdateVariantValue(Guid productId, Guid valueId, [FromBody] UpdateVariantValueRequest req)
    {
        if (req.Value != null && string.IsNullOrWhiteSpace(req.Value))
            return BadRequest(ApiResponse<bool>.Fail("INVALID_VALUE", "Escribe el nombre de la opción"));
        if (req.Stock is < 0)
            return BadRequest(ApiResponse<bool>.Fail("INVALID_STOCK", "El stock no puede ser negativo"));
        if (req.PriceModifier is < -10000 or > 10000)
            return BadRequest(ApiResponse<bool>.Fail("INVALID_PRICE", "El recargo está fuera de rango"));
        if (req.Value != null && req.Value.Length > 100)
            return BadRequest(ApiResponse<bool>.Fail("INVALID_VALUE", "La opción puede tener hasta 100 caracteres"));

        using var conn = _db.CreateConnection();
        var filas = await conn.ExecuteAsync(@"
            UPDATE product_variant_values SET
                value = COALESCE(@Value, value),
                image_url = CASE WHEN @ImageUrl IS NULL THEN image_url ELSE NULLIF(@ImageUrl, '') END,
                price_modifier = COALESCE(@PriceModifier, price_modifier),
                stock = COALESCE(@Stock, stock),
                is_active = COALESCE(@IsActive, is_active),
                color_hex = CASE WHEN @ColorHex IS NULL THEN color_hex ELSE NULLIF(@ColorHex, '') END
            WHERE id = @Id
              AND variant_type_id IN (SELECT id FROM product_variant_types WHERE product_id = @ProductId)",
            new { Id = valueId, ProductId = productId, Value = req.Value?.Trim(), req.ImageUrl, req.PriceModifier, req.Stock, req.IsActive, req.ColorHex });

        if (filas == 0) return NotFound(ApiResponse<bool>.Fail("NOT_FOUND", "Opción no encontrada"));
        return Ok(ApiResponse<bool>.Ok(true));
    }

    // DELETE variant type (cascades to values)
    [HttpDelete("types/{typeId}")]
    [Authorize(Roles = AppRoles.Staff)]
    public async Task<IActionResult> DeleteVariantType(Guid productId, Guid typeId)
    {
        using var conn = _db.CreateConnection();
        await conn.ExecuteAsync("DELETE FROM product_variant_types WHERE id = @Id AND product_id = @ProductId",
            new { Id = typeId, ProductId = productId });
        return Ok(ApiResponse<bool>.Ok(true));
    }

    // DELETE variant value
    [HttpDelete("values/{valueId}")]
    [Authorize(Roles = AppRoles.Staff)]
    public async Task<IActionResult> DeleteVariantValue(Guid productId, Guid valueId)
    {
        using var conn = _db.CreateConnection();
        await conn.ExecuteAsync(@"DELETE FROM product_variant_values WHERE id = @Id
            AND variant_type_id IN (SELECT id FROM product_variant_types WHERE product_id = @ProductId)", new { Id = valueId, ProductId = productId });
        return Ok(ApiResponse<bool>.Ok(true));
    }

    // POST upload variant image
    [HttpPost("values/{valueId}/image")]
    [Authorize(Roles = AppRoles.Staff)]
    [RequestSizeLimit(Archivos.MaxBytesSubida)]
    [RequestFormLimits(MultipartBodyLengthLimit = Archivos.MaxBytesSubida)]
    public async Task<IActionResult> UploadVariantImage(Guid productId, Guid valueId, [FromForm] IFormFile file)
    {
        if (file == null) return BadRequest(ApiResponse<object>.Fail("NO_FILE", "Sube una imagen"));
        var info = await Archivos.Validar(file, Archivos.Imagenes);
        if (info.Error != null) return BadRequest(ApiResponse<object>.Fail("INVALID_FILE", info.Error));

        var storage = HttpContext.RequestServices.GetRequiredService<UtilesApi.Infrastructure.Storage.IStorageService>();
        await using var stream = file.OpenReadStream();
        var imageUrl = await storage.UploadFileAsync(stream, info.NombreSeguro, info.ContentType);

        using var conn = _db.CreateConnection();
        await conn.ExecuteAsync("UPDATE product_variant_values SET image_url = @Url WHERE id = @Id",
            new { Url = imageUrl, Id = valueId });

        return Ok(ApiResponse<string>.Ok(imageUrl));
    }

    private const string ColumnasImagen =
        @"id, image_url AS ""imageUrl"", alt_text AS ""altText"", is_primary AS ""isPrimary"", display_order AS ""displayOrder""";

    // GET product images gallery → [{id, imageUrl, altText, isPrimary, displayOrder}]
    [HttpGet("~/api/products/{productId}/images")]
    [AllowAnonymous]
    public async Task<IActionResult> GetImages(Guid productId)
    {
        using var conn = _db.CreateConnection();
        var images = await conn.QueryAsync<dynamic>($@"
            SELECT {ColumnasImagen}
            FROM product_images WHERE product_id = @ProductId
            ORDER BY is_primary DESC, display_order", new { ProductId = productId });

        return Ok(ApiResponse<object>.Ok(images));
    }

    // POST upload product image (multipart 'file') → la imagen creada. La primera foto queda como principal.
    [HttpPost("~/api/products/{productId}/images")]
    [Authorize(Roles = AppRoles.Staff)]
    [RequestSizeLimit(Archivos.MaxBytesSubida)]
    [RequestFormLimits(MultipartBodyLengthLimit = Archivos.MaxBytesSubida)]
    public async Task<IActionResult> UploadImage(Guid productId, [FromForm] IFormFile file, [FromForm] string? altText, [FromForm] bool isPrimary = false)
    {
        if (file == null || file.Length == 0) return BadRequest(ApiResponse<object>.Fail("NO_FILE", "Sube una imagen"));
        var info = await Archivos.Validar(file, Archivos.Imagenes);
        if (info.Error != null) return BadRequest(ApiResponse<object>.Fail("INVALID_TYPE", info.Error));
        if (!Validacion.Largo(altText, 200)) return BadRequest(ApiResponse<object>.Fail("INVALID", "El texto alternativo puede tener hasta 200 caracteres"));

        using var conn = _db.CreateConnection();
        var existe = await conn.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM products WHERE id = @Id", new { Id = productId });
        if (existe == 0) return NotFound(ApiResponse<object>.Fail("NOT_FOUND", "Producto no encontrado"));

        var storage = HttpContext.RequestServices.GetRequiredService<UtilesApi.Infrastructure.Storage.IStorageService>();
        await using var stream = file.OpenReadStream();
        var imageUrl = await storage.UploadFileAsync(stream, info.NombreSeguro, info.ContentType);

        conn.Open();
        using var tx = conn.BeginTransaction();
        var hayPrincipal = await conn.ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM product_images WHERE product_id = @Id AND is_primary", new { Id = productId }, tx) > 0;
        var principal = isPrimary || !hayPrincipal;
        if (principal)
        {
            await conn.ExecuteAsync("UPDATE product_images SET is_primary = false WHERE product_id = @Id", new { Id = productId }, tx);
            await conn.ExecuteAsync("UPDATE products SET image_url = @Url, updated_at = NOW() WHERE id = @Id", new { Url = imageUrl, Id = productId }, tx);
        }

        var creada = await conn.QuerySingleAsync<dynamic>($@"
            INSERT INTO product_images (product_id, image_url, alt_text, is_primary, display_order)
            VALUES (@ProductId, @ImageUrl, @AltText, @IsPrimary,
                    COALESCE((SELECT MAX(display_order) + 1 FROM product_images WHERE product_id = @ProductId), 0))
            RETURNING {ColumnasImagen}", new { ProductId = productId, ImageUrl = imageUrl, AltText = altText, IsPrimary = principal }, tx);
        tx.Commit();

        return Ok(ApiResponse<object>.Ok(creada));
    }

    // DELETE product image. Si era la principal, pasa a serlo la siguiente (y products.image_url la sigue).
    [HttpDelete("~/api/products/{productId}/images/{imageId}")]
    [Authorize(Roles = AppRoles.Staff)]
    public async Task<IActionResult> DeleteImage(Guid productId, Guid imageId)
    {
        using var conn = _db.CreateConnection();
        conn.Open();
        using var tx = conn.BeginTransaction();
        var borrada = await conn.QueryFirstOrDefaultAsync<ImagenFila>(
            "DELETE FROM product_images WHERE id = @Id AND product_id = @ProductId RETURNING id, image_url, is_primary",
            new { Id = imageId, ProductId = productId }, tx);
        if (borrada == null) return NotFound(ApiResponse<bool>.Fail("NOT_FOUND", "Imagen no encontrada"));

        if (borrada.IsPrimary)
        {
            var siguiente = await conn.QueryFirstOrDefaultAsync<ImagenFila>(
                "SELECT id, image_url FROM product_images WHERE product_id = @ProductId ORDER BY display_order, created_at LIMIT 1",
                new { ProductId = productId }, tx);
            if (siguiente != null)
            {
                await conn.ExecuteAsync("UPDATE product_images SET is_primary = (id = @Id) WHERE product_id = @ProductId",
                    new { Id = siguiente.Id, ProductId = productId }, tx);
                await conn.ExecuteAsync("UPDATE products SET image_url = @Url, updated_at = NOW() WHERE id = @ProductId",
                    new { Url = siguiente.ImageUrl, ProductId = productId }, tx);
            }
            else
            {
                await conn.ExecuteAsync("UPDATE products SET image_url = NULL, updated_at = NOW() WHERE id = @ProductId AND image_url = @Url",
                    new { Url = borrada.ImageUrl, ProductId = productId }, tx);
            }
        }
        tx.Commit();
        return Ok(ApiResponse<bool>.Ok(true));
    }

    // PUT marca una imagen como principal (y la copia a products.image_url)
    [HttpPut("~/api/products/{productId}/images/{imageId}/primary")]
    [Authorize(Roles = AppRoles.Staff)]
    public async Task<IActionResult> SetPrimaryImage(Guid productId, Guid imageId)
    {
        using var conn = _db.CreateConnection();
        conn.Open();
        using var tx = conn.BeginTransaction();
        var url = await conn.ExecuteScalarAsync<string?>(
            "SELECT image_url FROM product_images WHERE id = @Id AND product_id = @ProductId", new { Id = imageId, ProductId = productId }, tx);
        if (url == null) return NotFound(ApiResponse<bool>.Fail("NOT_FOUND", "Imagen no encontrada"));

        await conn.ExecuteAsync("UPDATE product_images SET is_primary = (id = @Id) WHERE product_id = @ProductId",
            new { Id = imageId, ProductId = productId }, tx);
        await conn.ExecuteAsync("UPDATE products SET image_url = @Url, updated_at = NOW() WHERE id = @ProductId",
            new { Url = url, ProductId = productId }, tx);
        tx.Commit();
        return Ok(ApiResponse<bool>.Ok(true));
    }
}

public class ImagenFila
{
    public Guid Id { get; set; }
    public string ImageUrl { get; set; } = "";
    public bool IsPrimary { get; set; }
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
