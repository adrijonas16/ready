using Microsoft.AspNetCore.Mvc;
using Dapper;
using UtilesApi.DTOs;
using UtilesApi.Infrastructure.Database;

namespace UtilesApi.Controllers;

[ApiController]
[Route("api/campaigns")]
public class CampaignsController : ControllerBase
{
    private readonly IDbConnectionFactory _db;

    public CampaignsController(IDbConnectionFactory db) { _db = db; }

    [HttpGet]
    public async Task<IActionResult> GetAll([FromQuery] bool? activeOnly)
    {
        using var conn = _db.CreateConnection();
        var sql = activeOnly == true
            ? "SELECT * FROM campaigns WHERE is_active = true AND (end_date IS NULL OR end_date >= NOW()) ORDER BY created_at DESC"
            : "SELECT * FROM campaigns ORDER BY created_at DESC";
        var campaigns = await conn.QueryAsync<dynamic>(sql);
        return Ok(ApiResponse<object>.Ok(campaigns));
    }

    [HttpGet("{slug}")]
    public async Task<IActionResult> GetBySlug(string slug)
    {
        using var conn = _db.CreateConnection();
        var campaign = await conn.QueryFirstOrDefaultAsync<dynamic>("SELECT * FROM campaigns WHERE slug = @Slug", new { Slug = slug });
        if (campaign == null) return NotFound(ApiResponse<object>.Fail("NOT_FOUND", "Campana no encontrada"));

        var products = await conn.QueryAsync<dynamic>(@"
            SELECT p.*, cp.custom_discount_percent, cp.custom_price,
                   CASE WHEN cp.custom_price IS NOT NULL THEN cp.custom_price
                        WHEN cp.custom_discount_percent IS NOT NULL THEN p.base_price * (1 - cp.custom_discount_percent/100)
                        ELSE p.base_price * (1 - @Discount/100) END as sale_price
            FROM campaign_products cp
            JOIN products p ON p.id = cp.product_id AND p.is_active = true
            WHERE cp.campaign_id = @Id", new { Id = (Guid)campaign.id, Discount = (decimal)campaign.discount_percent });

        return Ok(ApiResponse<object>.Ok(new { campaign, products }));
    }

    [HttpPost]
    public async Task<IActionResult> Create([FromBody] CreateCampaignRequest req)
    {
        using var conn = _db.CreateConnection();
        var id = await conn.ExecuteScalarAsync<Guid>(@"
            INSERT INTO campaigns (name, slug, description, image_url, discount_percent, discount_fixed, start_date, end_date)
            VALUES (@Name, @Slug, @Description, @ImageUrl, @DiscountPercent, @DiscountFixed, @StartDate, @EndDate)
            RETURNING id", req);
        return Ok(ApiResponse<Guid>.Ok(id));
    }

    [HttpPut("{id}")]
    public async Task<IActionResult> Update(Guid id, [FromBody] CreateCampaignRequest req)
    {
        using var conn = _db.CreateConnection();
        await conn.ExecuteAsync(@"UPDATE campaigns SET name=@Name, slug=@Slug, description=@Description,
            image_url=@ImageUrl, discount_percent=@DiscountPercent, discount_fixed=@DiscountFixed,
            start_date=@StartDate, end_date=@EndDate WHERE id=@Id", new { Id = id, req.Name, req.Slug, req.Description, req.ImageUrl, req.DiscountPercent, req.DiscountFixed, req.StartDate, req.EndDate });
        return Ok(ApiResponse<bool>.Ok(true));
    }

    [HttpPost("{id}/products")]
    public async Task<IActionResult> AddProducts(Guid id, [FromBody] AddCampaignProductsRequest req)
    {
        using var conn = _db.CreateConnection();
        foreach (var pid in req.ProductIds)
        {
            await conn.ExecuteAsync(@"INSERT INTO campaign_products (campaign_id, product_id, custom_discount_percent, custom_price)
                VALUES (@CampaignId, @ProductId, @Discount, @Price) ON CONFLICT DO NOTHING",
                new { CampaignId = id, ProductId = pid, Discount = req.CustomDiscountPercent, Price = req.CustomPrice });
        }
        return Ok(ApiResponse<bool>.Ok(true));
    }

    [HttpDelete("{id}/products/{productId}")]
    public async Task<IActionResult> RemoveProduct(Guid id, Guid productId)
    {
        using var conn = _db.CreateConnection();
        await conn.ExecuteAsync("DELETE FROM campaign_products WHERE campaign_id=@Cid AND product_id=@Pid", new { Cid = id, Pid = productId });
        return Ok(ApiResponse<bool>.Ok(true));
    }

    [HttpPost("{id}/image")]
    public async Task<IActionResult> UploadImage(Guid id, [FromForm] IFormFile file)
    {
        var storage = HttpContext.RequestServices.GetRequiredService<UtilesApi.Infrastructure.Storage.IStorageService>();
        var url = await storage.UploadFileAsync(file.OpenReadStream(), file.FileName, file.ContentType);
        using var conn = _db.CreateConnection();
        await conn.ExecuteAsync("UPDATE campaigns SET image_url=@Url WHERE id=@Id", new { Url = url, Id = id });
        return Ok(ApiResponse<string>.Ok(url));
    }

    // NEWS
    [HttpGet("~/api/news")]
    public async Task<IActionResult> GetNews([FromQuery] bool? activeOnly)
    {
        using var conn = _db.CreateConnection();
        var sql = activeOnly == true
            ? "SELECT n.*, c.name as campaign_name, c.slug as campaign_slug FROM news n LEFT JOIN campaigns c ON c.id = n.campaign_id WHERE n.is_active = true ORDER BY n.display_order"
            : "SELECT n.*, c.name as campaign_name, c.slug as campaign_slug FROM news n LEFT JOIN campaigns c ON c.id = n.campaign_id ORDER BY n.display_order";
        return Ok(ApiResponse<object>.Ok(await conn.QueryAsync<dynamic>(sql)));
    }

    [HttpPost("~/api/news")]
    public async Task<IActionResult> CreateNews([FromBody] CreateNewsRequest req)
    {
        using var conn = _db.CreateConnection();
        var id = await conn.ExecuteScalarAsync<Guid>(@"INSERT INTO news (title, content, image_url, link_url, campaign_id, display_order)
            VALUES (@Title, @Content, @ImageUrl, @LinkUrl, @CampaignId, @DisplayOrder) RETURNING id", req);
        return Ok(ApiResponse<Guid>.Ok(id));
    }

    [HttpPut("~/api/news/{id}")]
    public async Task<IActionResult> UpdateNews(Guid id, [FromBody] CreateNewsRequest req)
    {
        using var conn = _db.CreateConnection();
        await conn.ExecuteAsync(@"UPDATE news SET title=@Title, content=@Content, image_url=@ImageUrl,
            link_url=@LinkUrl, campaign_id=@CampaignId, display_order=@DisplayOrder WHERE id=@Id",
            new { Id = id, req.Title, req.Content, req.ImageUrl, req.LinkUrl, req.CampaignId, req.DisplayOrder });
        return Ok(ApiResponse<bool>.Ok(true));
    }

    [HttpDelete("~/api/news/{id}")]
    public async Task<IActionResult> DeleteNews(Guid id)
    {
        using var conn = _db.CreateConnection();
        await conn.ExecuteAsync("DELETE FROM news WHERE id=@Id", new { Id = id });
        return Ok(ApiResponse<bool>.Ok(true));
    }

    [HttpPost("~/api/news/{id}/image")]
    public async Task<IActionResult> UploadNewsImage(Guid id, [FromForm] IFormFile file)
    {
        var storage = HttpContext.RequestServices.GetRequiredService<UtilesApi.Infrastructure.Storage.IStorageService>();
        var url = await storage.UploadFileAsync(file.OpenReadStream(), file.FileName, file.ContentType);
        using var conn = _db.CreateConnection();
        await conn.ExecuteAsync("UPDATE news SET image_url=@Url WHERE id=@Id", new { Url = url, Id = id });
        return Ok(ApiResponse<string>.Ok(url));
    }
}

public class CreateCampaignRequest
{
    public string Name { get; set; } = "";
    public string Slug { get; set; } = "";
    public string? Description { get; set; }
    public string? ImageUrl { get; set; }
    public decimal DiscountPercent { get; set; }
    public decimal DiscountFixed { get; set; }
    public DateTime? StartDate { get; set; }
    public DateTime? EndDate { get; set; }
}

public class AddCampaignProductsRequest
{
    public List<Guid> ProductIds { get; set; } = new();
    public decimal? CustomDiscountPercent { get; set; }
    public decimal? CustomPrice { get; set; }
}

public class CreateNewsRequest
{
    public string Title { get; set; } = "";
    public string? Content { get; set; }
    public string? ImageUrl { get; set; }
    public string? LinkUrl { get; set; }
    public Guid? CampaignId { get; set; }
    public int DisplayOrder { get; set; }
}
