using Microsoft.AspNetCore.Mvc;
using Dapper;
using UtilesApi.DTOs;
using UtilesApi.Infrastructure.Database;

namespace UtilesApi.Controllers;

[ApiController]
[Route("api/brands")]
public class BrandsController : ControllerBase
{
    private readonly IDbConnectionFactory _db;

    public BrandsController(IDbConnectionFactory db) { _db = db; }

    [HttpGet]
    public async Task<IActionResult> GetAll()
    {
        using var conn = _db.CreateConnection();
        var brands = await conn.QueryAsync<dynamic>(
            "SELECT id, name, logo_url, is_active FROM brands WHERE is_active = true ORDER BY name");
        return Ok(ApiResponse<object>.Ok(brands));
    }

    [HttpPost]
    public async Task<IActionResult> Create([FromBody] CreateBrandRequest req)
    {
        using var conn = _db.CreateConnection();
        var id = await conn.ExecuteScalarAsync<Guid>(
            "INSERT INTO brands (name, logo_url) VALUES (@Name, @LogoUrl) ON CONFLICT (name) DO NOTHING RETURNING id",
            new { req.Name, req.LogoUrl });
        return Ok(ApiResponse<Guid>.Ok(id));
    }

    [HttpPut("{id}")]
    public async Task<IActionResult> Update(Guid id, [FromBody] CreateBrandRequest req)
    {
        using var conn = _db.CreateConnection();
        await conn.ExecuteAsync(
            "UPDATE brands SET name = @Name, logo_url = @LogoUrl WHERE id = @Id",
            new { Id = id, req.Name, req.LogoUrl });
        return Ok(ApiResponse<bool>.Ok(true));
    }

    [HttpPost("{id}/logo")]
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
    public string? LogoUrl { get; set; }
}
