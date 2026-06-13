using Microsoft.AspNetCore.Mvc;
using UtilesApi.Core.Entities;
using UtilesApi.DTOs;
using UtilesApi.Infrastructure.Database;
using UtilesApi.Infrastructure.Storage;
using UtilesApi.Services;

namespace UtilesApi.Controllers;

[ApiController]
[Route("api/lists")]
public class ListsController : ControllerBase
{
    private readonly SupplyListRepository _listRepo;
    private readonly SupplyItemRepository _itemRepo;
    private readonly SchoolRepository _schoolRepo;
    private readonly GradeRepository _gradeRepo;
    private readonly ProductRepository _productRepo;
    private readonly IStorageService _storage;
    private readonly ListProcessingService _processingService;

    public ListsController(
        SupplyListRepository listRepo,
        SupplyItemRepository itemRepo,
        SchoolRepository schoolRepo,
        GradeRepository gradeRepo,
        ProductRepository productRepo,
        IStorageService storage,
        ListProcessingService processingService)
    {
        _listRepo = listRepo;
        _itemRepo = itemRepo;
        _schoolRepo = schoolRepo;
        _gradeRepo = gradeRepo;
        _productRepo = productRepo;
        _storage = storage;
        _processingService = processingService;
    }

    [HttpPost("upload")]
    public async Task<ActionResult<ApiResponse<ListResponse>>> Upload([FromForm] List<IFormFile> files, [FromForm] Guid userId, [FromForm] Guid schoolId, [FromForm] Guid gradeId, [FromForm] int year, [FromForm] string? submittedBy)
    {
        if (files == null || files.Count == 0)
            return BadRequest(ApiResponse<ListResponse>.Fail("NO_FILE", "Debe subir al menos una imagen"));

        if (files.Count > 5)
            return BadRequest(ApiResponse<ListResponse>.Fail("TOO_MANY", "Maximo 5 imagenes"));

        var allowedTypes = new[] { "image/jpeg", "image/png", "image/webp", "application/pdf", "application/msword", "application/vnd.openxmlformats-officedocument.wordprocessingml.document" };
        var imageUrls = new List<string>();
        foreach (var file in files)
        {
            if (!allowedTypes.Contains(file.ContentType))
                return BadRequest(ApiResponse<ListResponse>.Fail("INVALID_TYPE", $"Solo se permiten imagenes, PDF o Word. Archivo invalido: {file.FileName}"));
            var url = await _storage.UploadFileAsync(file.OpenReadStream(), file.FileName, file.ContentType);
            imageUrls.Add(url);
        }
        var imageUrl = string.Join("|", imageUrls);

        // Validate user exists, set null if not found
        Guid? validUserId = null;
        if (userId != Guid.Empty)
        {
            var userRepo = HttpContext.RequestServices.GetRequiredService<UserRepository>();
            var existingUser = await userRepo.GetById(userId);
            if (existingUser != null) validUserId = userId;
        }

        var list = new SupplyList
        {
            UserId = validUserId,
            SchoolId = schoolId,
            GradeId = gradeId,
            Year = year,
            ImageUrl = imageUrl,
            Estado = ListStatus.PENDIENTE_REVISION,
            EsOficial = false,
            SubmittedBy = submittedBy,
            FechaSubida = DateTime.UtcNow,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };

        var createdId = await _listRepo.Create(list);
        list.Id = createdId;

        // Process OCR and match products in background
        _ = Task.Run(async () =>
        {
            try { await _processingService.ProcessList(createdId); }
            catch (Exception ex) { Console.WriteLine($"OCR processing error: {ex.Message}"); }
        });

        var school = await _schoolRepo.GetById(schoolId);
        var grade = await _gradeRepo.GetById(gradeId);

        return Ok(ApiResponse<ListResponse>.Ok(new ListResponse
        {
            Id = createdId,
            UserId = list.UserId,
            SchoolId = list.SchoolId,
            SchoolName = school?.Name ?? "",
            GradeId = list.GradeId,
            GradeName = grade?.Name ?? "",
            Year = list.Year,
            ImageUrl = list.ImageUrl,
            Estado = list.Estado.ToString(),
            EsOficial = list.EsOficial,
            SubmittedBy = list.SubmittedBy,
            FechaSubida = list.FechaSubida,
            CreatedAt = list.CreatedAt
        }));
    }

    [HttpGet("{id}")]
    public async Task<ActionResult<ApiResponse<ListDetailResponse>>> GetById(Guid id)
    {
        var list = await _listRepo.GetById(id);
        if (list == null)
            return NotFound(ApiResponse<ListDetailResponse>.Fail("NOT_FOUND", "Lista no encontrada"));

        var items = await _itemRepo.GetByListId(id);
        var school = await _schoolRepo.GetById(list.SchoolId);
        var grade = await _gradeRepo.GetById(list.GradeId);

        var allProductIds = items
            .SelectMany(i => new[] { i.MatchedProductId, i.ProductEconomicoId, i.ProductMedioId, i.ProductPremiumId })
            .Where(id => id.HasValue).Select(id => id!.Value).Distinct();
        var matchedProducts = allProductIds.Any()
            ? (await Task.WhenAll(allProductIds.Select(pid => _productRepo.GetById(pid)))).Where(p => p != null).ToDictionary(p => p!.Id, p => p!)
            : new Dictionary<Guid, Product>();

        ProductResponse? MapProduct(Guid? id) => id.HasValue && matchedProducts.ContainsKey(id.Value)
            ? new ProductResponse { Id = matchedProducts[id.Value].Id, Name = matchedProducts[id.Value].Name, Description = matchedProducts[id.Value].Description, Category = matchedProducts[id.Value].Category, Brand = matchedProducts[id.Value].Brand, Sku = matchedProducts[id.Value].Sku, BasePrice = matchedProducts[id.Value].BasePrice, ImageUrl = matchedProducts[id.Value].ImageUrl, Stock = matchedProducts[id.Value].Stock, Rating = matchedProducts[id.Value].Rating, Tier = matchedProducts[id.Value].Tier }
            : null;

        return Ok(ApiResponse<ListDetailResponse>.Ok(new ListDetailResponse
        {
            List = new ListResponse
            {
                Id = list.Id,
                UserId = list.UserId,
                SchoolId = list.SchoolId,
                SchoolName = school?.Name ?? "",
                GradeId = list.GradeId,
                GradeName = grade?.Name ?? "",
                Year = list.Year,
                ImageUrl = list.ImageUrl,
                OcrText = list.OcrText,
                Estado = list.Estado.ToString(),
                EsOficial = list.EsOficial,
                Observaciones = list.Observaciones,
                SubmittedBy = list.SubmittedBy,
                Plan = list.Plan,
                EstudianteNombre = list.EstudianteNombre,
                EstudianteGrado = list.EstudianteGrado,
                FechaSubida = list.FechaSubida,
                FechaInicioRevision = list.FechaInicioRevision,
                FechaValidacion = list.FechaValidacion,
                CreatedAt = list.CreatedAt
            },
            Items = items.Select(i => new SupplyItemResponse
            {
                Id = i.Id,
                SupplyListId = i.SupplyListId,
                NombreOriginal = i.NombreOriginal,
                NombreDetectado = i.NombreDetectado,
                Cantidad = i.Cantidad,
                Notas = i.Notas,
                MatchedProductId = i.MatchedProductId,
                MatchedQuantity = i.MatchedQuantity,
                PriceAtMatch = i.PriceAtMatch,
                MatchedProduct = MapProduct(i.MatchedProductId),
                ProductEconomicoId = i.ProductEconomicoId,
                ProductEconomico = MapProduct(i.ProductEconomicoId),
                PriceEconomico = i.PriceEconomico,
                ProductMedioId = i.ProductMedioId,
                ProductMedio = MapProduct(i.ProductMedioId),
                PriceMedio = i.PriceMedio,
                ProductPremiumId = i.ProductPremiumId,
                ProductPremium = MapProduct(i.ProductPremiumId),
                PricePremium = i.PricePremium,
                Forro = i.Forro,
                ForroColor = i.ForroColor,
                Etiqueta = i.Etiqueta,
                EtiquetaDibujo = i.EtiquetaDibujo,
                Caratula = i.Caratula,
                CaratulaCurso = i.CaratulaCurso,
                DatosEstudiante = i.DatosEstudiante
            }).ToList()
        }));
    }

    [HttpGet]
    public async Task<ActionResult<ApiResponse<IEnumerable<ListResponse>>>> GetAll([FromQuery] string? estado, [FromQuery] bool? esOficial, [FromQuery] Guid? userId)
    {
        var lists = await _listRepo.GetAll(estado, esOficial, userId);
        var result = new List<ListResponse>();

        foreach (var list in lists)
        {
            var school = await _schoolRepo.GetById(list.SchoolId);
            var grade = await _gradeRepo.GetById(list.GradeId);
            result.Add(new ListResponse
            {
                Id = list.Id,
                UserId = list.UserId,
                SchoolId = list.SchoolId,
                SchoolName = school?.Name ?? "",
                GradeId = list.GradeId,
                GradeName = grade?.Name ?? "",
                Year = list.Year,
                ImageUrl = list.ImageUrl,
                Estado = list.Estado.ToString(),
                EsOficial = list.EsOficial,
                Observaciones = list.Observaciones,
                SubmittedBy = list.SubmittedBy,
                FechaSubida = list.FechaSubida,
                FechaValidacion = list.FechaValidacion,
                CreatedAt = list.CreatedAt
            });
        }

        return Ok(ApiResponse<IEnumerable<ListResponse>>.Ok(result));
    }

    [HttpGet("official")]
    public async Task<ActionResult<ApiResponse<IEnumerable<ListResponse>>>> GetOfficial([FromQuery] Guid? schoolId, [FromQuery] Guid? gradeId)
    {
        var lists = await _listRepo.GetOfficialLists(schoolId, gradeId);
        var result = new List<ListResponse>();

        foreach (var list in lists)
        {
            var school = await _schoolRepo.GetById(list.SchoolId);
            var grade = await _gradeRepo.GetById(list.GradeId);
            result.Add(new ListResponse
            {
                Id = list.Id,
                SchoolId = list.SchoolId,
                SchoolName = school?.Name ?? "",
                GradeId = list.GradeId,
                GradeName = grade?.Name ?? "",
                Year = list.Year,
                ImageUrl = list.ImageUrl,
                Estado = list.Estado.ToString(),
                EsOficial = true,
                CreatedAt = list.CreatedAt
            });
        }

        return Ok(ApiResponse<IEnumerable<ListResponse>>.Ok(result));
    }

    [HttpDelete("{id}")]
    public async Task<IActionResult> DeleteList(Guid id)
    {
        using var conn = HttpContext.RequestServices.GetRequiredService<IDbConnectionFactory>().CreateConnection();
        await Dapper.SqlMapper.ExecuteAsync(conn, "DELETE FROM supply_items WHERE supply_list_id = @Id; DELETE FROM supply_lists WHERE id = @Id", new { Id = id });
        return Ok(ApiResponse<bool>.Ok(true));
    }

    [HttpPut("{id}/status")]
    public async Task<ActionResult<ApiResponse<bool>>> UpdateStatus(Guid id, [FromBody] UpdateListStatusRequest request)
    {
        if (!Enum.TryParse<ListStatus>(request.Status, out var status))
            return BadRequest(ApiResponse<bool>.Fail("INVALID_STATUS", "Estado invalido"));

        await _listRepo.UpdateStatus(id, status, request.Observaciones);

        if (status == ListStatus.VALIDADA)
        {
            _ = Task.Run(() => _processingService.ProcessMatching(id));
        }

        return Ok(ApiResponse<bool>.Ok(true));
    }

    [HttpPut("{id}/items/{itemId}")]
    public async Task<ActionResult<ApiResponse<bool>>> UpdateItem(Guid id, Guid itemId, [FromBody] UpdateSupplyItemRequest request)
    {
        var items = await _itemRepo.GetByListId(id);
        var item = items.FirstOrDefault(i => i.Id == itemId);
        if (item == null)
            return NotFound(ApiResponse<bool>.Fail("ITEM_NOT_FOUND", "Item no encontrado"));

        if (request.ProductId.HasValue)
        {
            item.MatchedProductId = request.ProductId;
        }
        if (request.NombreDetectado != null)
        {
            item.NombreDetectado = request.NombreDetectado;
        }
        if (request.Cantidad.HasValue)
        {
            item.Cantidad = request.Cantidad.Value;
            item.MatchedQuantity = request.Cantidad.Value;
        }
        if (request.Notas != null)
        {
            item.Notas = request.Notas;
        }
        if (request.UserCustomQuantity.HasValue)
        {
            item.UserCustomQuantity = request.UserCustomQuantity;
        }
        if (request.UserNotas != null)
        {
            item.UserNotas = request.UserNotas;
        }
        if (request.Forro.HasValue) item.Forro = request.Forro.Value;
        if (request.ForroColor != null) item.ForroColor = request.ForroColor;
        if (request.Etiqueta != null) item.Etiqueta = request.Etiqueta;
        if (request.EtiquetaDibujo.HasValue) item.EtiquetaDibujo = request.EtiquetaDibujo.Value;
        if (request.Caratula.HasValue) item.Caratula = request.Caratula.Value;
        if (request.CaratulaCurso != null) item.CaratulaCurso = request.CaratulaCurso;
        if (request.DatosEstudiante != null) item.DatosEstudiante = request.DatosEstudiante;
        if (request.NombreOriginal != null) item.NombreOriginal = request.NombreOriginal;

        // Tier-specific product assignments
        if (request.ProductEconomicoId.HasValue)
        {
            item.ProductEconomicoId = request.ProductEconomicoId;
            var p = await _productRepo.GetById(request.ProductEconomicoId.Value);
            if (p != null) item.PriceEconomico = p.BasePrice;
        }
        if (request.ProductMedioId.HasValue)
        {
            item.ProductMedioId = request.ProductMedioId;
            var p = await _productRepo.GetById(request.ProductMedioId.Value);
            if (p != null) item.PriceMedio = p.BasePrice;
        }
        if (request.ProductPremiumId.HasValue)
        {
            item.ProductPremiumId = request.ProductPremiumId;
            var p = await _productRepo.GetById(request.ProductPremiumId.Value);
            if (p != null) item.PricePremium = p.BasePrice;
        }

        await _itemRepo.Update(item);
        return Ok(ApiResponse<bool>.Ok(true));
    }

    [HttpPut("{id}/plan")]
    public async Task<ActionResult<ApiResponse<bool>>> UpdatePlan(Guid id, [FromBody] UpdateListPlanRequest request)
    {
        var list = await _listRepo.GetById(id);
        if (list == null) return NotFound(ApiResponse<bool>.Fail("NOT_FOUND", "Lista no encontrada"));

        // Update plan and student info
        using var conn = HttpContext.RequestServices.GetRequiredService<IDbConnectionFactory>().CreateConnection();
        await Dapper.SqlMapper.ExecuteAsync(conn, @"
            UPDATE supply_lists SET plan = @Plan, estudiante_nombre = @Nombre, estudiante_grado = @Grado, updated_at = NOW()
            WHERE id = @Id", new { Id = id, Plan = request.Plan, Nombre = request.EstudianteNombre, Grado = request.EstudianteGrado });

        // Re-match products based on tier preference
        var items = await _itemRepo.GetByListId(id);
        foreach (var item in items)
        {
            if (item.MatchedProductId.HasValue)
            {
                var currentProduct = await _productRepo.GetById(item.MatchedProductId.Value);
                if (currentProduct != null && currentProduct.Tier != request.Plan)
                {
                    // Find a product in the same category matching the tier
                    var alternatives = await _productRepo.Search(item.NombreOriginal, null, 20);
                    var tierMatch = alternatives.FirstOrDefault(p => p.Tier == request.Plan && p.Category == currentProduct.Category)
                        ?? alternatives.FirstOrDefault(p => p.Tier == request.Plan);
                    if (tierMatch != null)
                    {
                        item.MatchedProductId = tierMatch.Id;
                        item.PriceAtMatch = tierMatch.BasePrice;
                        await _itemRepo.Update(item);
                    }
                }
            }
        }

        return Ok(ApiResponse<bool>.Ok(true));
    }

    [HttpPost("{id}/observaciones")]
    public async Task<IActionResult> AddObservacion(Guid id, [FromBody] AddObservacionRequest req)
    {
        using var conn = HttpContext.RequestServices.GetRequiredService<IDbConnectionFactory>().CreateConnection();
        await Dapper.SqlMapper.ExecuteAsync(conn,
            "UPDATE supply_lists SET user_observaciones = COALESCE(user_observaciones, '') || E'\\n' || @Obs, updated_at = NOW() WHERE id = @Id",
            new { Id = id, Obs = $"[{DateTime.UtcNow:yyyy-MM-dd HH:mm}] {req.Observacion}" });
        return Ok(ApiResponse<bool>.Ok(true));
    }

    [HttpPost("from-text")]
    public async Task<IActionResult> CreateFromText([FromBody] CreateFromTextRequest request)
    {
        var school = await _schoolRepo.GetById(request.SchoolId);
        if (school == null) return BadRequest(ApiResponse<object>.Fail("NOT_FOUND", "Colegio no encontrado"));

        var grade = await _gradeRepo.GetById(request.GradeId);
        if (grade == null) return BadRequest(ApiResponse<object>.Fail("NOT_FOUND", "Grado no encontrado"));

        // Validate user exists, set null if not
        Guid? userId = null;
        if (request.UserId != Guid.Empty)
        {
            var userRepo = HttpContext.RequestServices.GetRequiredService<UserRepository>();
            var existingUser = await userRepo.GetById(request.UserId);
            if (existingUser != null) userId = request.UserId;
        }

        var list = new SupplyList
        {
            UserId = userId,
            SchoolId = request.SchoolId,
            GradeId = request.GradeId,
            Year = request.Year,
            Estado = ListStatus.PENDIENTE_REVISION,
            EsOficial = false,
            FechaSubida = DateTime.UtcNow,
            CreatedAt = DateTime.UtcNow,
        };

        var listId = await _listRepo.Create(list);
        list.Id = listId;

        foreach (var item in request.Items)
        {
            var supplyItem = new SupplyItem
            {
                SupplyListId = listId,
                NombreOriginal = item.NombreOriginal,
                NombreDetectado = item.NombreOriginal,
                Cantidad = item.Cantidad > 0 ? item.Cantidad : 1,
                Notas = item.Notas,
                CreatedAt = DateTime.UtcNow,
            };
            await _itemRepo.Create(supplyItem);
        }

        // Try auto-matching products
        _ = Task.Run(async () =>
        {
            try { await _processingService.ProcessMatching(listId); }
            catch { /* best effort */ }
        });

        return Ok(ApiResponse<ListResponse>.Ok(new ListResponse
        {
            Id = list.Id,
            UserId = list.UserId,
            SchoolId = list.SchoolId,
            SchoolName = school.Name,
            GradeId = list.GradeId,
            GradeName = grade.Name,
            Year = list.Year,
            Estado = list.Estado.ToString(),
            EsOficial = false,
            FechaSubida = list.FechaSubida,
            CreatedAt = list.CreatedAt,
        }));
    }
}

public class CreateFromTextRequest
{
    public Guid UserId { get; set; }
    public Guid SchoolId { get; set; }
    public Guid GradeId { get; set; }
    public int Year { get; set; }
    public List<CreateFromTextItem> Items { get; set; } = new();
}

public class CreateFromTextItem
{
    public string NombreOriginal { get; set; } = "";
    public int Cantidad { get; set; } = 1;
    public string? Notas { get; set; }
}

public class AddObservacionRequest
{
    public string Observacion { get; set; } = "";
}