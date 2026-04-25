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
    public async Task<ActionResult<ApiResponse<ListResponse>>> Upload([FromForm] IFormFile file, [FromForm] Guid userId, [FromForm] Guid schoolId, [FromForm] Guid gradeId, [FromForm] int year, [FromForm] string? submittedBy)
    {
        if (file == null || file.Length == 0)
            return BadRequest(ApiResponse<ListResponse>.Fail("NO_FILE", "Debe subir una imagen"));

        var allowedTypes = new[] { "image/jpeg", "image/png", "image/webp" };
        if (!allowedTypes.Contains(file.ContentType))
            return BadRequest(ApiResponse<ListResponse>.Fail("INVALID_TYPE", "Solo se permiten imagenes JPEG, PNG o WebP"));

        var imageUrl = await _storage.UploadFileAsync(file.OpenReadStream(), file.FileName, file.ContentType);

        var list = new SupplyList
        {
            Id = Guid.NewGuid(),
            UserId = userId,
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

        await _listRepo.Create(list);

        var school = await _schoolRepo.GetById(schoolId);
        var grade = await _gradeRepo.GetById(gradeId);

        return Ok(ApiResponse<ListResponse>.Ok(new ListResponse
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

        var matchedProductIds = items.Where(i => i.MatchedProductId.HasValue).Select(i => i.MatchedProductId!.Value).Distinct();
        var matchedProducts = matchedProductIds.Any() 
            ? (await Task.WhenAll(matchedProductIds.Select(pid => _productRepo.GetById(pid)))).Where(p => p != null).ToDictionary(p => p!.Id, p => p!)
            : new Dictionary<Guid, Product>();

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
                MatchedProduct = i.MatchedProductId.HasValue && matchedProducts.ContainsKey(i.MatchedProductId.Value)
                    ? new ProductResponse
                    {
                        Id = matchedProducts[i.MatchedProductId.Value].Id,
                        Name = matchedProducts[i.MatchedProductId.Value].Name,
                        Description = matchedProducts[i.MatchedProductId.Value].Description,
                        Category = matchedProducts[i.MatchedProductId.Value].Category,
                        Brand = matchedProducts[i.MatchedProductId.Value].Brand,
                        Sku = matchedProducts[i.MatchedProductId.Value].Sku,
                        BasePrice = matchedProducts[i.MatchedProductId.Value].BasePrice,
                        ImageUrl = matchedProducts[i.MatchedProductId.Value].ImageUrl,
                        Stock = matchedProducts[i.MatchedProductId.Value].Stock
                    }
                    : null
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

        await _itemRepo.Update(item);
        return Ok(ApiResponse<bool>.Ok(true));
    }
}