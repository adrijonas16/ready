using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using System.Text.Json.Serialization;
using Dapper;
using UtilesApi.DTOs;
using UtilesApi.Infrastructure.Database;
using UtilesApi.Services;

namespace UtilesApi.Controllers;

[ApiController]
[Route("api/campaigns")]
public class CampaignsController : ControllerBase
{
    private readonly IDbConnectionFactory _db;

    public CampaignsController(IDbConnectionFactory db) { _db = db; }

    [HttpGet]
    [AllowAnonymous]
    public async Task<IActionResult> GetAll([FromQuery] bool? activeOnly)
    {
        using var conn = _db.CreateConnection();
        var sql = activeOnly == true
            ? "SELECT * FROM campaigns WHERE is_active = true AND (start_date IS NULL OR start_date <= NOW()) AND (end_date IS NULL OR end_date >= NOW()) ORDER BY created_at DESC"
            : "SELECT * FROM campaigns ORDER BY created_at DESC";
        var campaigns = await conn.QueryAsync<dynamic>(sql);
        return Ok(ApiResponse<object>.Ok(campaigns));
    }

    [HttpGet("{slug}")]
    [AllowAnonymous]
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
    [Authorize(Roles = AppRoles.Staff)]
    public async Task<IActionResult> Create([FromBody] CreateCampaignRequest req)
    {
        if (string.IsNullOrWhiteSpace(req.Name) || string.IsNullOrWhiteSpace(req.Slug))
            return BadRequest(ApiResponse<Guid>.Fail("INVALID_CAMPAIGN", "La campaña necesita nombre y slug"));
        var error = ValidarDescuento(req.DiscountPercent, req.DiscountFixed) ?? ValidarFechas(req);
        if (error != null) return BadRequest(ApiResponse<Guid>.Fail("INVALID_CAMPAIGN", error));

        using var conn = _db.CreateConnection();
        try
        {
            var id = await conn.ExecuteScalarAsync<Guid>(@"
                INSERT INTO campaigns (name, slug, description, image_url, discount_percent, discount_fixed, start_date, end_date, is_active)
                VALUES (@Name, @Slug, @Description, @ImageUrl, @DiscountPercent, @DiscountFixed, @StartDate::timestamptz, @EndDate::timestamptz, @IsActive)
                RETURNING id", new
            {
                Name = req.Name!.Trim(), Slug = req.Slug!.Trim(), req.Description, req.ImageUrl,
                DiscountPercent = req.DiscountPercent ?? 0, DiscountFixed = req.DiscountFixed ?? 0,
                StartDate = Validacion.AUtcTexto(Validacion.LeerFecha(req.StartDate)), EndDate = Validacion.AUtcTexto(Validacion.LeerFecha(req.EndDate), finDeDia: true),
                IsActive = req.IsActive ?? true
            });
            return Ok(ApiResponse<Guid>.Ok(id));
        }
        catch (Npgsql.PostgresException ex) when (ex.SqlState == Npgsql.PostgresErrorCodes.UniqueViolation)
        {
            return Conflict(ApiResponse<Guid>.Fail("SLUG_EXISTS", "Ya existe una campaña con ese slug"));
        }
    }

    /// <summary>
    /// Edita la campaña. Los campos que no vienen (o vienen null) no cambian, salvo startDate/endDate:
    /// si vienen en el JSON con null, se quitan (campaña sin fecha de inicio/fin).
    /// </summary>
    [HttpPut("{id}")]
    [Authorize(Roles = AppRoles.Staff)]
    public async Task<IActionResult> Update(Guid id, [FromBody] CreateCampaignRequest req)
    {
        var error = ValidarDescuento(req.DiscountPercent, req.DiscountFixed) ?? ValidarFechas(req);
        if (error != null) return BadRequest(ApiResponse<bool>.Fail("INVALID_CAMPAIGN", error));

        using var conn = _db.CreateConnection();
        try
        {
            var filas = await conn.ExecuteAsync(@"UPDATE campaigns SET
                name = COALESCE(NULLIF(@Name, ''), name),
                slug = COALESCE(NULLIF(@Slug, ''), slug),
                description = COALESCE(@Description, description),
                image_url = CASE WHEN @ImageUrl IS NULL THEN image_url ELSE NULLIF(@ImageUrl, '') END,
                discount_percent = COALESCE(@DiscountPercent, discount_percent),
                discount_fixed = COALESCE(@DiscountFixed, discount_fixed),
                start_date = CASE WHEN @StartDateSet THEN @StartDate::timestamptz ELSE start_date END,
                end_date = CASE WHEN @EndDateSet THEN @EndDate::timestamptz ELSE end_date END,
                is_active = COALESCE(@IsActive, is_active)
                WHERE id = @Id", new
            {
                Id = id, Name = req.Name?.Trim(), Slug = req.Slug?.Trim(), req.Description, req.ImageUrl,
                req.DiscountPercent, req.DiscountFixed,
                StartDateSet = req.StartDateProvided, StartDate = Validacion.AUtcTexto(Validacion.LeerFecha(req.StartDate)),
                EndDateSet = req.EndDateProvided, EndDate = Validacion.AUtcTexto(Validacion.LeerFecha(req.EndDate), finDeDia: true),
                req.IsActive
            });
            if (filas == 0) return NotFound(ApiResponse<bool>.Fail("NOT_FOUND", "Campaña no encontrada"));
            return Ok(ApiResponse<bool>.Ok(true));
        }
        catch (Npgsql.PostgresException ex) when (ex.SqlState == Npgsql.PostgresErrorCodes.UniqueViolation)
        {
            return Conflict(ApiResponse<bool>.Fail("SLUG_EXISTS", "Ya existe una campaña con ese slug"));
        }
    }

    /// <summary>Borra la campaña, sus productos asociados y desvincula las noticias que la enlazaban.</summary>
    [HttpDelete("{id}")]
    [Authorize(Roles = AppRoles.Staff)]
    public async Task<IActionResult> Delete(Guid id)
    {
        using var conn = _db.CreateConnection();
        conn.Open();
        using var tx = conn.BeginTransaction();
        await conn.ExecuteAsync("UPDATE news SET campaign_id = NULL WHERE campaign_id = @Id", new { Id = id }, tx);
        await conn.ExecuteAsync("DELETE FROM campaign_products WHERE campaign_id = @Id", new { Id = id }, tx);
        var filas = await conn.ExecuteAsync("DELETE FROM campaigns WHERE id = @Id", new { Id = id }, tx);
        if (filas == 0) return NotFound(ApiResponse<bool>.Fail("NOT_FOUND", "Campaña no encontrada"));
        tx.Commit();
        return Ok(ApiResponse<bool>.Ok(true));
    }

    /// <summary>Agrega productos a la campaña: { productIds: [...], customDiscountPercent?, customPrice? }.</summary>
    [HttpPost("{id}/products")]
    [Authorize(Roles = AppRoles.Staff)]
    public async Task<IActionResult> AddProducts(Guid id, [FromBody] AddCampaignProductsRequest req)
    {
        var ids = (req.ProductIds ?? new List<Guid>()).Where(x => x != Guid.Empty).Distinct().ToArray();
        if (req.ProductId.HasValue && req.ProductId.Value != Guid.Empty && !ids.Contains(req.ProductId.Value))
            ids = ids.Append(req.ProductId.Value).ToArray();
        if (ids.Length == 0)
            return BadRequest(ApiResponse<int>.Fail("NO_PRODUCTS", "Elige al menos un producto"));

        using var conn = _db.CreateConnection();
        var existe = await conn.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM campaigns WHERE id = @Id", new { Id = id });
        if (existe == 0) return NotFound(ApiResponse<int>.Fail("NOT_FOUND", "Campaña no encontrada"));

        await conn.ExecuteAsync(@"
            UPDATE campaign_products SET
                custom_discount_percent = COALESCE(@Discount, custom_discount_percent),
                custom_price = COALESCE(@Price, custom_price)
            WHERE campaign_id = @CampaignId AND product_id = ANY(@Ids);
            INSERT INTO campaign_products (campaign_id, product_id, custom_discount_percent, custom_price)
            SELECT @CampaignId, p.id, @Discount, @Price FROM products p
            WHERE p.id = ANY(@Ids)
              AND NOT EXISTS (SELECT 1 FROM campaign_products x WHERE x.campaign_id = @CampaignId AND x.product_id = p.id)",
            new { CampaignId = id, Ids = ids, Discount = req.CustomDiscountPercent, Price = req.CustomPrice });
        return Ok(ApiResponse<bool>.Ok(true));
    }

    private static string? ValidarFechas(CreateCampaignRequest req)
    {
        if (!string.IsNullOrWhiteSpace(req.StartDate) && Validacion.LeerFecha(req.StartDate) == null) return "Fecha de inicio inválida";
        if (!string.IsNullOrWhiteSpace(req.EndDate) && Validacion.LeerFecha(req.EndDate) == null) return "Fecha de fin inválida";
        return null;
    }

    private static string? ValidarDescuento(decimal? percent, decimal? fixedAmount)
    {
        if (percent is < 0 or > 100) return "El descuento debe estar entre 0 y 100 %";
        if (fixedAmount is < 0) return "El descuento fijo no puede ser negativo";
        return null;
    }

    [HttpDelete("{id}/products/{productId}")]
    [Authorize(Roles = AppRoles.Staff)]
    public async Task<IActionResult> RemoveProduct(Guid id, Guid productId)
    {
        using var conn = _db.CreateConnection();
        await conn.ExecuteAsync("DELETE FROM campaign_products WHERE campaign_id=@Cid AND product_id=@Pid", new { Cid = id, Pid = productId });
        return Ok(ApiResponse<bool>.Ok(true));
    }

    [HttpPost("{id}/image")]
    [Authorize(Roles = AppRoles.Staff)]
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
    [AllowAnonymous]
    public async Task<IActionResult> GetNews([FromQuery] bool? activeOnly)
    {
        using var conn = _db.CreateConnection();
        var sql = activeOnly == true
            ? "SELECT n.*, c.name as campaign_name, c.slug as campaign_slug FROM news n LEFT JOIN campaigns c ON c.id = n.campaign_id WHERE n.is_active = true ORDER BY n.display_order"
            : "SELECT n.*, c.name as campaign_name, c.slug as campaign_slug FROM news n LEFT JOIN campaigns c ON c.id = n.campaign_id ORDER BY n.display_order";
        return Ok(ApiResponse<object>.Ok(await conn.QueryAsync<dynamic>(sql)));
    }

    [HttpPost("~/api/news")]
    [Authorize(Roles = AppRoles.Staff)]
    public async Task<IActionResult> CreateNews([FromBody] CreateNewsRequest req)
    {
        if (string.IsNullOrWhiteSpace(req.Title))
            return BadRequest(ApiResponse<Guid>.Fail("INVALID_TITLE", "Escribe el título de la noticia"));
        using var conn = _db.CreateConnection();
        var campaignId = await ResolverCampana(conn, req);
        var id = await conn.ExecuteScalarAsync<Guid>(@"INSERT INTO news (title, content, image_url, link_url, campaign_id, display_order, is_active)
            VALUES (@Title, @Content, @ImageUrl, @LinkUrl, @CampaignId, @DisplayOrder, @IsActive) RETURNING id",
            new { Title = req.Title.Trim(), req.Content, ImageUrl = string.IsNullOrWhiteSpace(req.ImageUrl) ? null : req.ImageUrl,
                  LinkUrl = string.IsNullOrWhiteSpace(req.LinkUrl) ? null : req.LinkUrl, CampaignId = campaignId,
                  DisplayOrder = req.DisplayOrder ?? 0, IsActive = req.IsActive ?? true });
        return Ok(ApiResponse<Guid>.Ok(id));
    }

    /// <summary>
    /// Edita la noticia: {title, content, imageUrl, linkUrl, campaignId, displayOrder, isActive}.
    /// Lo que no viene no cambia. campaignId: null en el JSON = quitar el enlace. imageUrl/linkUrl: "" = quitar.
    /// </summary>
    [HttpPut("~/api/news/{id}")]
    [Authorize(Roles = AppRoles.Staff)]
    public async Task<IActionResult> UpdateNews(Guid id, [FromBody] CreateNewsRequest req)
    {
        if (req.Title != null && string.IsNullOrWhiteSpace(req.Title))
            return BadRequest(ApiResponse<bool>.Fail("INVALID_TITLE", "Escribe el título de la noticia"));
        using var conn = _db.CreateConnection();
        var campaignId = await ResolverCampana(conn, req);
        var cambiaCampana = req.CampaignIdProvided || !string.IsNullOrWhiteSpace(req.CampaignSlug);
        var filas = await conn.ExecuteAsync(@"UPDATE news SET
            title = COALESCE(@Title, title),
            content = COALESCE(@Content, content),
            image_url = CASE WHEN @ImageUrl IS NULL THEN image_url ELSE NULLIF(@ImageUrl, '') END,
            link_url = CASE WHEN @LinkUrl IS NULL THEN link_url ELSE NULLIF(@LinkUrl, '') END,
            campaign_id = CASE WHEN @CambiaCampana THEN @CampaignId ELSE campaign_id END,
            display_order = COALESCE(@DisplayOrder, display_order),
            is_active = COALESCE(@IsActive, is_active)
            WHERE id = @Id",
            new { Id = id, Title = req.Title?.Trim(), req.Content, req.ImageUrl, req.LinkUrl, CambiaCampana = cambiaCampana,
                  CampaignId = campaignId, req.DisplayOrder, req.IsActive });
        if (filas == 0) return NotFound(ApiResponse<bool>.Fail("NOT_FOUND", "Noticia no encontrada"));
        return Ok(ApiResponse<bool>.Ok(true));
    }

    /// <summary>campaignId tiene prioridad; si no viene, se acepta campaignSlug (lo que manda el servidor demo).</summary>
    private static async Task<Guid?> ResolverCampana(System.Data.IDbConnection conn, CreateNewsRequest req)
    {
        if (Guid.TryParse(req.CampaignId, out var cid) && cid != Guid.Empty) return cid;
        if (!string.IsNullOrWhiteSpace(req.CampaignSlug))
            return await conn.ExecuteScalarAsync<Guid?>("SELECT id FROM campaigns WHERE slug = @Slug", new { Slug = req.CampaignSlug.Trim() });
        return null;
    }

    [HttpDelete("~/api/news/{id}")]
    [Authorize(Roles = AppRoles.Staff)]
    public async Task<IActionResult> DeleteNews(Guid id)
    {
        using var conn = _db.CreateConnection();
        await conn.ExecuteAsync("DELETE FROM news WHERE id=@Id", new { Id = id });
        return Ok(ApiResponse<bool>.Ok(true));
    }

    [HttpPost("~/api/news/{id}/image")]
    [Authorize(Roles = AppRoles.Staff)]
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
    private string? _startDate;
    private string? _endDate;

    public string? Name { get; set; }
    public string? Slug { get; set; }
    public string? Description { get; set; }
    /// <summary>null = no cambia (PUT); "" = quitar</summary>
    public string? ImageUrl { get; set; }
    public decimal? DiscountPercent { get; set; }
    public decimal? DiscountFixed { get; set; }
    public bool? IsActive { get; set; }
    /// <summary>Fecha ISO ("2026-03-01" se toma como hora de Lima). "" o null = sin fecha.</summary>
    public string? StartDate { get => _startDate; set { _startDate = value; StartDateProvided = true; } }
    public string? EndDate { get => _endDate; set { _endDate = value; EndDateProvided = true; } }

    /// <summary>true si el JSON trae startDate (aunque sea null)</summary>
    [JsonIgnore] public bool StartDateProvided { get; private set; }
    [JsonIgnore] public bool EndDateProvided { get; private set; }
}

public class AddCampaignProductsRequest
{
    public List<Guid>? ProductIds { get; set; } = new();
    /// <summary>Compatibilidad con el cliente antiguo (un solo producto)</summary>
    public Guid? ProductId { get; set; }
    public decimal? CustomDiscountPercent { get; set; }
    public decimal? CustomPrice { get; set; }
}

public class CreateNewsRequest
{
    private string? _campaignId;

    public string? Title { get; set; }
    public string? Content { get; set; }
    public string? ImageUrl { get; set; }
    public string? LinkUrl { get; set; }
    /// <summary>Id de la campaña enlazada (texto: "" o null = sin campaña)</summary>
    public string? CampaignId { get => _campaignId; set { _campaignId = value; CampaignIdProvided = true; } }
    public string? CampaignSlug { get; set; }
    public int? DisplayOrder { get; set; }
    public bool? IsActive { get; set; }

    [JsonIgnore] public bool CampaignIdProvided { get; private set; }
}
