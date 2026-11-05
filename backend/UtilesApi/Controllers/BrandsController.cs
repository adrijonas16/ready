using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Dapper;
using UtilesApi.DTOs;
using UtilesApi.Infrastructure.Database;
using UtilesApi.Services;

namespace UtilesApi.Controllers;

[ApiController]
[Route("api/brands")]
public class BrandsController : ControllerBase
{
    private readonly IDbConnectionFactory _db;

    public BrandsController(IDbConnectionFactory db) { _db = db; }

    private const string Columnas = @"id, name, logo_url AS ""logoUrl"", is_active AS ""isActive""";

    [HttpGet]
    [AllowAnonymous]
    public async Task<IActionResult> GetAll()
    {
        using var conn = _db.CreateConnection();
        var brands = await conn.QueryAsync<dynamic>(
            $"SELECT {Columnas} FROM brands WHERE is_active = true ORDER BY name");
        return Ok(ApiResponse<object>.Ok(brands));
    }

    /// <summary>Crea la marca (o reactiva una borrada con el mismo nombre). Devuelve su id.</summary>
    [HttpPost]
    [Authorize(Roles = AppRoles.Staff)]
    public async Task<IActionResult> Create([FromBody] CreateBrandRequest req)
    {
        if (string.IsNullOrWhiteSpace(req.Name))
            return BadRequest(ApiResponse<Guid>.Fail("INVALID_NAME", "Escribe el nombre de la marca"));

        using var conn = _db.CreateConnection();
        var activa = await conn.ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM brands WHERE LOWER(name) = LOWER(@Name) AND is_active = true", new { Name = req.Name.Trim() });
        if (activa > 0)
            return Conflict(ApiResponse<Guid>.Fail("BRAND_EXISTS", "Ya existe una marca con ese nombre"));

        var id = await conn.ExecuteScalarAsync<Guid>(@"
            INSERT INTO brands (name, logo_url) VALUES (@Name, @LogoUrl)
            ON CONFLICT (name) DO UPDATE SET is_active = TRUE, logo_url = COALESCE(EXCLUDED.logo_url, brands.logo_url)
            RETURNING id", new { Name = req.Name.Trim(), LogoUrl = string.IsNullOrWhiteSpace(req.LogoUrl) ? null : req.LogoUrl });
        return Ok(ApiResponse<Guid>.Ok(id));
    }

    /// <summary>Renombra (también en products.brand). LogoUrl null = no cambia; "" = quitar logo.</summary>
    [HttpPut("{id}")]
    [Authorize(Roles = AppRoles.Staff)]
    public async Task<IActionResult> Update(Guid id, [FromBody] CreateBrandRequest req)
    {
        if (string.IsNullOrWhiteSpace(req.Name))
            return BadRequest(ApiResponse<bool>.Fail("INVALID_NAME", "Escribe el nombre de la marca"));
        var nombre = req.Name.Trim();

        using var conn = _db.CreateConnection();
        conn.Open();
        using var tx = conn.BeginTransaction();
        var actual = await conn.ExecuteScalarAsync<string?>("SELECT name FROM brands WHERE id = @Id FOR UPDATE", new { Id = id }, tx);
        if (actual == null) return NotFound(ApiResponse<bool>.Fail("NOT_FOUND", "Marca no encontrada"));

        if (actual != nombre)
        {
            var duplicada = await conn.ExecuteScalarAsync<int>(
                "SELECT COUNT(*) FROM brands WHERE LOWER(name) = LOWER(@Name) AND id <> @Id", new { Id = id, Name = nombre }, tx);
            if (duplicada > 0) return Conflict(ApiResponse<bool>.Fail("BRAND_EXISTS", "Ya existe una marca con ese nombre"));
        }

        await conn.ExecuteAsync(@"
            UPDATE brands SET name = @Name,
                logo_url = CASE WHEN @LogoUrl IS NULL THEN logo_url ELSE NULLIF(@LogoUrl, '') END
            WHERE id = @Id", new { Id = id, Name = nombre, req.LogoUrl }, tx);
        if (actual != nombre)
            await conn.ExecuteAsync("UPDATE products SET brand = @Name, updated_at = NOW() WHERE brand = @Old",
                new { Name = nombre, Old = actual }, tx);
        tx.Commit();
        return Ok(ApiResponse<bool>.Ok(true));
    }

    /// <summary>
    /// Borra la marca de la lista de marcas (queda inactiva para que no reaparezca al reiniciar,
    /// porque al arrancar se registran las marcas que usan los productos). Los productos conservan su marca.
    /// </summary>
    [HttpDelete("{id}")]
    [Authorize(Roles = AppRoles.Staff)]
    public async Task<IActionResult> Delete(Guid id)
    {
        using var conn = _db.CreateConnection();
        var filas = await conn.ExecuteAsync("UPDATE brands SET is_active = false WHERE id = @Id", new { Id = id });
        if (filas == 0) return NotFound(ApiResponse<bool>.Fail("NOT_FOUND", "Marca no encontrada"));
        return Ok(ApiResponse<bool>.Ok(true));
    }

    [HttpPost("{id}/logo")]
    [Authorize(Roles = AppRoles.Staff)]
    public async Task<IActionResult> UploadLogo(Guid id, [FromForm] IFormFile file)
    {
        if (file == null) return BadRequest(ApiResponse<object>.Fail("NO_FILE", "Sube un logo"));
        var storage = HttpContext.RequestServices.GetRequiredService<UtilesApi.Infrastructure.Storage.IStorageService>();
        var url = await storage.UploadFileAsync(file.OpenReadStream(), file.FileName, file.ContentType);
        using var conn = _db.CreateConnection();
        await conn.ExecuteAsync("UPDATE brands SET logo_url = @Url WHERE id = @Id", new { Url = url, Id = id });
        return Ok(ApiResponse<string>.Ok(url));
    }
}

public class CreateBrandRequest
{
    public string Name { get; set; } = "";
    /// <summary>null = no cambia (PUT); "" = quitar</summary>
    public string? LogoUrl { get; set; }
}
