using Microsoft.AspNetCore.Authorization;
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
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<ListsController> _logger;
    private readonly INotificationService _notifications;

    public ListsController(
        SupplyListRepository listRepo,
        SupplyItemRepository itemRepo,
        SchoolRepository schoolRepo,
        GradeRepository gradeRepo,
        ProductRepository productRepo,
        IStorageService storage,
        ListProcessingService processingService,
        IServiceScopeFactory scopeFactory,
        ILogger<ListsController> logger,
        INotificationService notifications)
    {
        _listRepo = listRepo;
        _itemRepo = itemRepo;
        _schoolRepo = schoolRepo;
        _gradeRepo = gradeRepo;
        _productRepo = productRepo;
        _storage = storage;
        _processingService = processingService;
        _scopeFactory = scopeFactory;
        _logger = logger;
        _notifications = notifications;
    }

    /// <summary>
    /// Ejecuta el procesamiento en segundo plano con su propio scope de DI
    /// (los servicios del request ya no son válidos cuando la respuesta termina).
    /// </summary>
    private void RunInBackground(Guid listId, Func<ListProcessingService, Task> work)
    {
        _ = Task.Run(async () =>
        {
            using var scope = _scopeFactory.CreateScope();
            var service = scope.ServiceProvider.GetRequiredService<ListProcessingService>();
            try { await work(service); }
            catch (Exception ex) { _logger.LogError(ex, "Error procesando la lista {ListId}", listId); }
        });
    }

    /// <summary>Lista oficial/compartida del colegio (no es copia personal y ya está aprobada).</summary>
    private static bool EsListaPublica(SupplyList l) =>
        l.CopiaDe == null && (l.EsOficial || l.Estado == ListStatus.VALIDADA || l.Estado == ListStatus.PROCESADA);

    /// <summary>
    /// Quién puede cambiar útiles, plan y extras de una lista: ADMIN/OPERATOR siempre; si la lista tiene dueño,
    /// solo el dueño; las listas públicas del colegio no se tocan (el cliente trabaja sobre su copia);
    /// las listas sin dueño que no son públicas (subidas o copiadas sin sesión) sí, porque su id solo lo tiene quien la creó.
    /// </summary>
    private bool PuedeEditar(SupplyList l)
    {
        if (User.IsStaff()) return true;
        if (l.UserId.HasValue) return User.GetUserId() == l.UserId;
        return !EsListaPublica(l);
    }

    private ActionResult SinPermiso<T>() => User.GetUserId() == null
        ? Unauthorized(ApiResponse<T>.Fail("UNAUTHORIZED", "Inicia sesión para cambiar esta lista"))
        : StatusCode(403, ApiResponse<T>.Fail("FORBIDDEN", "Esta lista no es tuya: haz una copia para cambiarla"));

    private static ListResponse Mapear(SupplyList list, string schoolName, string gradeName, bool detalle) => new()
    {
        Id = list.Id,
        UserId = list.UserId,
        SchoolId = list.SchoolId,
        SchoolName = schoolName,
        GradeId = list.GradeId,
        GradeName = gradeName,
        Year = list.Year,
        ImageUrl = list.ImageUrl,
        OcrText = detalle ? list.OcrText : null,
        Estado = list.Estado.ToString(),
        EsOficial = list.EsOficial,
        Observaciones = list.Observaciones,
        SubmittedBy = list.SubmittedBy,
        UserObservaciones = list.UserObservaciones,
        IaEstado = list.IaEstado,
        IaMensaje = list.IaMensaje,
        Extras = detalle ? list.Extras : null,
        Plan = list.Plan,
        EstudianteNombre = list.EstudianteNombre,
        EstudianteGrado = list.EstudianteGrado,
        CopiaDe = list.CopiaDe,
        FechaSubida = list.FechaSubida,
        FechaInicioRevision = list.FechaInicioRevision,
        FechaValidacion = list.FechaValidacion,
        CreatedAt = list.CreatedAt
    };

    [HttpPost("upload")]
    [AllowAnonymous]
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

        // La lista es de quien tiene la sesión (el userId del formulario se ignora: no se puede subir a nombre de otro)
        Guid? validUserId = User.GetUserId();

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

        // Leer la lista (IA) y emparejar productos en segundo plano.
        // El estado PROCESANDO se marca antes de responder para que el cliente pueda esperar el resultado.
        await _listRepo.SetIaEstado(createdId, "PROCESANDO");
        RunInBackground(createdId, s => s.ProcessList(createdId));

        var school = await _schoolRepo.GetById(schoolId);
        var grade = await _gradeRepo.GetById(gradeId);

        // Aviso al admin: hay una lista nueva para revisar y aprobar
        _ = _notifications.NotifyNewList(createdId.ToString(), school?.Name ?? "", grade?.Name ?? "")
            .ContinueWith(t => _logger.LogWarning(t.Exception, "No se pudo notificar la lista nueva"), TaskContinuationOptions.OnlyOnFaulted);

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
            IaEstado = "PROCESANDO",
            FechaSubida = list.FechaSubida,
            CreatedAt = list.CreatedAt
        }));
    }

    [HttpGet("{id}")]
    [AllowAnonymous]
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
        var matchedProducts = (await _productRepo.GetByIds(allProductIds)).ToDictionary(p => p.Id, p => p);

        ProductResponse? MapProduct(Guid? id) => id.HasValue && matchedProducts.ContainsKey(id.Value)
            ? ProductResponse.From(matchedProducts[id.Value])
            : null;

        return Ok(ApiResponse<ListDetailResponse>.Ok(new ListDetailResponse
        {
            List = Mapear(list, school?.Name ?? "", grade?.Name ?? "", detalle: true),
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
                UserCustomQuantity = i.UserCustomQuantity,
                UserNotas = i.UserNotas,
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
                DatosEstudiante = i.DatosEstudiante,
                Agregado = i.Agregado
            }).ToList()
        }));
    }

    /// <summary>
    /// Todas las listas (ADMIN/OPERATOR, con filtros estado/esOficial/userId).
    /// Un cliente logueado solo puede pedir las suyas (?userId=su id).
    /// </summary>
    [HttpGet]
    [Authorize]
    public async Task<ActionResult<ApiResponse<IEnumerable<ListResponse>>>> GetAll([FromQuery] string? estado, [FromQuery] bool? esOficial, [FromQuery] Guid? userId)
    {
        if (!User.IsStaff())
        {
            var propio = User.GetUserId();
            if (propio == null || userId != propio)
                return StatusCode(403, ApiResponse<IEnumerable<ListResponse>>.Fail("FORBIDDEN", "Solo puedes ver tus propias listas"));
        }

        var lists = await _listRepo.GetAll(estado, esOficial, userId);
        var result = new List<ListResponse>();
        var colegios = new Dictionary<Guid, string>();
        var grados = new Dictionary<Guid, string>();

        foreach (var list in lists)
        {
            if (!colegios.TryGetValue(list.SchoolId, out var colegio))
                colegios[list.SchoolId] = colegio = (await _schoolRepo.GetById(list.SchoolId))?.Name ?? "";
            if (!grados.TryGetValue(list.GradeId, out var grado))
                grados[list.GradeId] = grado = (await _gradeRepo.GetById(list.GradeId))?.Name ?? "";
            result.Add(Mapear(list, colegio, grado, detalle: false));
        }

        return Ok(ApiResponse<IEnumerable<ListResponse>>.Ok(result));
    }

    [HttpGet("official")]
    [AllowAnonymous]
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
    [Authorize(Roles = AppRoles.Staff)]
    public async Task<IActionResult> DeleteList(Guid id)
    {
        using var conn = HttpContext.RequestServices.GetRequiredService<IDbConnectionFactory>().CreateConnection();
        conn.Open();
        using var tx = conn.BeginTransaction();
        // Los pedidos y copias que apuntan a la lista la pierden como referencia, no se borran
        await Dapper.SqlMapper.ExecuteAsync(conn, "UPDATE orders SET supply_list_id = NULL WHERE supply_list_id = @Id", new { Id = id }, tx);
        await Dapper.SqlMapper.ExecuteAsync(conn, "UPDATE supply_lists SET copia_de = NULL WHERE copia_de = @Id", new { Id = id }, tx);
        await Dapper.SqlMapper.ExecuteAsync(conn, "DELETE FROM supply_items WHERE supply_list_id = @Id; DELETE FROM supply_lists WHERE id = @Id", new { Id = id }, tx);
        tx.Commit();
        return Ok(ApiResponse<bool>.Ok(true));
    }

    private static readonly ListStatus[] EstadosRevision =
        { ListStatus.PENDIENTE_REVISION, ListStatus.EN_REVISION, ListStatus.OBSERVADA, ListStatus.RECHAZADA, ListStatus.VALIDADA };

    /// <summary>
    /// Revisión del admin: {status, observaciones, esOficial?}. Se puede reabrir una lista validada (VALIDADA → EN_REVISION).
    /// observaciones null = no cambia, "" = borrar.
    /// </summary>
    [HttpPut("{id}/status")]
    [Authorize(Roles = AppRoles.Staff)]
    public async Task<ActionResult<ApiResponse<bool>>> UpdateStatus(Guid id, [FromBody] UpdateListStatusRequest request)
    {
        if (!Enum.TryParse<ListStatus>(request.Status, true, out var status) || !EstadosRevision.Contains(status))
            return BadRequest(ApiResponse<bool>.Fail("INVALID_STATUS", "Estado inválido: usa PENDIENTE_REVISION, EN_REVISION, OBSERVADA, RECHAZADA o VALIDADA"));

        var anterior = await _listRepo.GetById(id);
        if (anterior == null)
            return NotFound(ApiResponse<bool>.Fail("NOT_FOUND", "Lista no encontrada"));

        await _listRepo.UpdateStatus(id, status, request.Observaciones, request.EsOficial);

        if (status == ListStatus.VALIDADA && anterior.Estado != ListStatus.VALIDADA)
        {
            RunInBackground(id, s => s.ProcessMatching(id));

            // Aviso al cliente: ya puede elegir su opción y comprar
            if (anterior.UserId != null)
            {
                try { await _notifications.NotifyListReady(anterior.UserId.Value.ToString(), id.ToString()); }
                catch (Exception ex) { _logger.LogWarning(ex, "No se pudo notificar la aprobación de la lista {ListId}", id); }
            }
        }

        return Ok(ApiResponse<bool>.Ok(true));
    }

    /// <summary>Corrige colegio, grado o año de una lista: {schoolId?, gradeId?, year?}.</summary>
    [HttpPut("{id}")]
    [Authorize(Roles = AppRoles.Staff)]
    public async Task<ActionResult<ApiResponse<ListResponse>>> UpdateList(Guid id, [FromBody] UpdateListRequest request)
    {
        var list = await _listRepo.GetById(id);
        if (list == null) return NotFound(ApiResponse<ListResponse>.Fail("NOT_FOUND", "Lista no encontrada"));

        var schoolId = request.SchoolId ?? list.SchoolId;
        var school = await _schoolRepo.GetById(schoolId);
        if (school == null) return BadRequest(ApiResponse<ListResponse>.Fail("SCHOOL_NOT_FOUND", "Colegio no encontrado"));

        var gradeId = request.GradeId ?? list.GradeId;
        var grade = await _gradeRepo.GetById(gradeId);
        if (grade == null) return BadRequest(ApiResponse<ListResponse>.Fail("GRADE_NOT_FOUND", "Grado no encontrado"));
        if (grade.SchoolId != schoolId)
            return BadRequest(ApiResponse<ListResponse>.Fail("GRADE_MISMATCH", "Ese grado no pertenece al colegio elegido"));
        if (request.Year is < 2000 or > 2100)
            return BadRequest(ApiResponse<ListResponse>.Fail("INVALID_YEAR", "Año inválido"));

        await _listRepo.UpdateDatos(id, schoolId, gradeId, request.Year);
        var actualizada = await _listRepo.GetById(id);
        return Ok(ApiResponse<ListResponse>.Ok(Mapear(actualizada!, school.Name, grade.Name, detalle: false)));
    }

    [HttpPut("{id}/items/{itemId}")]
    [AllowAnonymous]
    public async Task<ActionResult<ApiResponse<bool>>> UpdateItem(Guid id, Guid itemId, [FromBody] UpdateSupplyItemRequest request)
    {
        var lista = await _listRepo.GetById(id);
        if (lista == null) return NotFound(ApiResponse<bool>.Fail("NOT_FOUND", "Lista no encontrada"));
        if (!PuedeEditar(lista)) return SinPermiso<bool>();

        var items = await _itemRepo.GetByListId(id);
        var item = items.FirstOrDefault(i => i.Id == itemId);
        if (item == null)
            return NotFound(ApiResponse<bool>.Fail("ITEM_NOT_FOUND", "Item no encontrado"));

        if (request.ProductId.HasValue)
        {
            item.MatchedProductId = request.ProductId;
            var elegido = await _productRepo.GetById(request.ProductId.Value);
            if (elegido != null) item.PriceAtMatch = elegido.BasePrice;
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

    [HttpPost("{id}/auto-match")]
    [Authorize(Roles = AppRoles.Staff)]
    public async Task<ActionResult<ApiResponse<bool>>> AutoMatch(Guid id, [FromQuery] bool force = false)
    {
        var items = await _itemRepo.GetByListId(id);
        foreach (var item in items)
        {
            var results = await _productRepo.Search(item.NombreOriginal, null, 20);

            var ecoMatch = results.FirstOrDefault(p => p.Tier == "economico") ?? results.FirstOrDefault();
            var medMatch = results.FirstOrDefault(p => p.Tier == "medio") ?? results.FirstOrDefault();
            var preMatch = results.FirstOrDefault(p => p.Tier == "premium") ?? results.FirstOrDefault();

            if (ecoMatch != null && (force || item.ProductEconomicoId == null)) { item.ProductEconomicoId = ecoMatch.Id; item.PriceEconomico = ecoMatch.BasePrice; }
            if (medMatch != null && (force || item.ProductMedioId == null)) { item.ProductMedioId = medMatch.Id; item.PriceMedio = medMatch.BasePrice; }
            if (preMatch != null && (force || item.ProductPremiumId == null)) { item.ProductPremiumId = preMatch.Id; item.PricePremium = preMatch.BasePrice; }

            if (medMatch != null && (force || item.MatchedProductId == null))
            {
                item.MatchedProductId = medMatch.Id;
                item.NombreDetectado = medMatch.Name;
                item.MatchedQuantity = item.Cantidad;
                item.PriceAtMatch = medMatch.BasePrice;
            }

            await _itemRepo.Update(item);
        }
        return Ok(ApiResponse<bool>.Ok(true));
    }

    /// <summary>Vuelve a leer la lista desde sus archivos (reemplaza los útiles detectados).</summary>
    [HttpPost("{id}/reprocess")]
    [Authorize(Roles = AppRoles.Staff)]
    public async Task<ActionResult<ApiResponse<bool>>> Reprocess(Guid id)
    {
        var list = await _listRepo.GetById(id);
        if (list == null) return NotFound(ApiResponse<bool>.Fail("NOT_FOUND", "Lista no encontrada"));
        if (string.IsNullOrWhiteSpace(list.ImageUrl))
            return BadRequest(ApiResponse<bool>.Fail("NO_FILE", "La lista no tiene archivos para leer"));

        await _listRepo.SetIaEstado(id, "PROCESANDO");
        RunInBackground(id, s => s.ProcessList(id));
        return Ok(ApiResponse<bool>.Ok(true));
    }

    /// <summary>Guarda los extras de la lista (forrado, etiquetas, carátulas y libros) como JSON.</summary>
    [HttpPut("{id}/extras")]
    [AllowAnonymous]
    public async Task<ActionResult<ApiResponse<bool>>> UpdateExtras(Guid id, [FromBody] System.Text.Json.JsonElement extras)
    {
        var list = await _listRepo.GetById(id);
        if (list == null) return NotFound(ApiResponse<bool>.Fail("NOT_FOUND", "Lista no encontrada"));
        if (!PuedeEditar(list)) return SinPermiso<bool>();
        if (extras.ValueKind != System.Text.Json.JsonValueKind.Object)
            return BadRequest(ApiResponse<bool>.Fail("INVALID", "Formato de extras inválido"));

        var json = extras.GetRawText();
        if (json.Length > 20000)
            return BadRequest(ApiResponse<bool>.Fail("TOO_LARGE", "Demasiados datos en los extras"));

        await _listRepo.UpdateExtras(id, json);
        return Ok(ApiResponse<bool>.Ok(true));
    }

    [HttpPost("{id}/items")]
    [AllowAnonymous]
    public async Task<ActionResult<ApiResponse<SupplyItemResponse>>> AddItem(Guid id, [FromBody] CreateFromTextItem request)
    {
        var list = await _listRepo.GetById(id);
        if (list == null) return NotFound(ApiResponse<SupplyItemResponse>.Fail("NOT_FOUND", "Lista no encontrada"));
        if (!PuedeEditar(list)) return SinPermiso<SupplyItemResponse>();
        if (string.IsNullOrWhiteSpace(request.NombreOriginal))
            return BadRequest(ApiResponse<SupplyItemResponse>.Fail("INVALID", "Escribe el nombre del útil"));

        var cantidad = request.Cantidad > 0 ? request.Cantidad : 1;
        var producto = request.ProductId.HasValue ? await _productRepo.GetById(request.ProductId.Value) : null;
        var item = new SupplyItem
        {
            SupplyListId = id,
            NombreOriginal = request.NombreOriginal.Trim(),
            NombreDetectado = producto?.Name ?? request.NombreOriginal.Trim(),
            Cantidad = cantidad,
            Notas = request.Notas,
            MatchedProductId = producto?.Id,
            MatchedQuantity = producto != null ? cantidad : null,
            PriceAtMatch = producto?.BasePrice,
            Agregado = request.PorCliente || producto != null,
            CreatedAt = DateTime.UtcNow,
        };
        item.Id = await _itemRepo.Create(item);

        return Ok(ApiResponse<SupplyItemResponse>.Ok(new SupplyItemResponse
        {
            Id = item.Id,
            SupplyListId = id,
            NombreOriginal = item.NombreOriginal,
            NombreDetectado = item.NombreDetectado,
            Cantidad = item.Cantidad,
            Notas = item.Notas,
            MatchedProductId = item.MatchedProductId,
            MatchedQuantity = item.MatchedQuantity,
            PriceAtMatch = item.PriceAtMatch,
            Agregado = item.Agregado,
        }));
    }

    [HttpDelete("{id}/items/{itemId}")]
    [AllowAnonymous]
    public async Task<ActionResult<ApiResponse<bool>>> DeleteItem(Guid id, Guid itemId)
    {
        var lista = await _listRepo.GetById(id);
        if (lista == null) return NotFound(ApiResponse<bool>.Fail("NOT_FOUND", "Lista no encontrada"));
        if (!PuedeEditar(lista)) return SinPermiso<bool>();

        var items = await _itemRepo.GetByListId(id);
        if (!items.Any(i => i.Id == itemId))
            return NotFound(ApiResponse<bool>.Fail("ITEM_NOT_FOUND", "Item no encontrado"));
        await _itemRepo.Delete(itemId);
        return Ok(ApiResponse<bool>.Ok(true));
    }

    /// <summary>
    /// Copia personal de una lista (normalmente la oficial del colegio) para que cada familia
    /// cambie productos, cantidades y extras sin tocar la lista que ven los demás.
    /// La copia es del usuario de la sesión (sin sesión queda sin dueño). Si ya tiene una copia de esa lista
    /// devuelve la misma (y le aplica el plan pedido si es otro), salvo nueva=true: crea otra (hermanos del mismo grado).
    /// </summary>
    [HttpPost("{id}/copia")]
    [AllowAnonymous]
    public async Task<ActionResult<ApiResponse<object>>> Copiar(Guid id, [FromBody] CopiarListaRequest? request)
    {
        request ??= new CopiarListaRequest();
        var list = await _listRepo.GetById(id);
        if (list == null) return NotFound(ApiResponse<object>.Fail("NOT_FOUND", "Lista no encontrada"));
        if (request.Plan != null && request.Plan is not ("economico" or "medio" or "premium"))
            return BadRequest(ApiResponse<object>.Fail("INVALID_PLAN", "Opción inválida: economico, medio o premium"));

        // copia_de siempre apunta a la lista del colegio (aunque se copie desde otra copia)
        var origenId = list.CopiaDe ?? id;
        var userId = User.GetUserId();

        using var conn = HttpContext.RequestServices.GetRequiredService<IDbConnectionFactory>().CreateConnection();
        if (userId.HasValue && request.Nueva != true)
        {
            var existente = await Dapper.SqlMapper.QueryFirstOrDefaultAsync<SupplyList>(conn,
                "SELECT * FROM supply_lists WHERE copia_de = @Id AND user_id = @UserId ORDER BY created_at DESC LIMIT 1",
                new { Id = origenId, UserId = userId });
            if (existente != null)
            {
                if (!string.IsNullOrWhiteSpace(request.Plan) && !string.Equals(existente.Plan, request.Plan, StringComparison.OrdinalIgnoreCase))
                    await UpdatePlan(existente.Id, new UpdateListPlanRequest
                    {
                        Plan = request.Plan, EstudianteNombre = existente.EstudianteNombre, EstudianteGrado = existente.EstudianteGrado, AplicarATodos = true
                    });
                return Ok(ApiResponse<object>.Ok(new { id = existente.Id, existente = true }));
            }
        }

        var nuevaId = await Dapper.SqlMapper.ExecuteScalarAsync<Guid>(conn, @"
            INSERT INTO supply_lists (user_id, school_id, grade_id, year, image_url, ocr_text, estado, es_oficial, submitted_by,
                                      fecha_subida, fecha_validacion, ia_estado, extras, copia_de)
            SELECT @UserId, school_id, grade_id, year, image_url, ocr_text, 'VALIDADA', FALSE, submitted_by,
                   NOW(), NOW(), ia_estado, extras, @Origen
            FROM supply_lists WHERE id = @Id
            RETURNING id", new { Id = id, Origen = origenId, UserId = userId });

        await Dapper.SqlMapper.ExecuteAsync(conn, @"
            INSERT INTO supply_items (supply_list_id, product_id, nombre_original, nombre_detectado, cantidad, notas,
                matched_product_id, matched_quantity, price_at_match, product_economico_id, product_medio_id, product_premium_id,
                price_economico, price_medio, price_premium, agregado, created_at)
            SELECT @Nueva, product_id, nombre_original, nombre_detectado, cantidad, notas,
                matched_product_id, matched_quantity, price_at_match, product_economico_id, product_medio_id, product_premium_id,
                price_economico, price_medio, price_premium, COALESCE(agregado, FALSE), created_at
            FROM supply_items WHERE supply_list_id = @Id", new { Id = id, Nueva = nuevaId });

        if (!string.IsNullOrWhiteSpace(request.Plan))
            await UpdatePlan(nuevaId, new UpdateListPlanRequest { Plan = request.Plan, AplicarATodos = true });

        return Ok(ApiResponse<object>.Ok(new { id = nuevaId, existente = false }));
    }

    [HttpPut("{id}/plan")]
    [AllowAnonymous]
    public async Task<ActionResult<ApiResponse<bool>>> UpdatePlan(Guid id, [FromBody] UpdateListPlanRequest request)
    {
        var list = await _listRepo.GetById(id);
        if (list == null) return NotFound(ApiResponse<bool>.Fail("NOT_FOUND", "Lista no encontrada"));
        if (!PuedeEditar(list)) return SinPermiso<bool>();
        if (request.Plan is not ("economico" or "medio" or "premium"))
            return BadRequest(ApiResponse<bool>.Fail("INVALID_PLAN", "Opción inválida: economico, medio o premium"));

        // Update plan and student info
        using var conn = HttpContext.RequestServices.GetRequiredService<IDbConnectionFactory>().CreateConnection();
        await Dapper.SqlMapper.ExecuteAsync(conn, @"
            UPDATE supply_lists SET plan = @Plan, estudiante_nombre = @Nombre, estudiante_grado = @Grado, updated_at = NOW()
            WHERE id = @Id", new { Id = id, Plan = request.Plan, Nombre = request.EstudianteNombre, Grado = request.EstudianteGrado });

        // Cambiar cada útil al producto del nivel elegido. Primero se usa el producto que la IA
        // (o el admin) asignó a ese nivel; si no hay, se busca uno parecido como antes.
        var aplicar = request.AplicarATodos ?? !string.Equals(list.Plan, request.Plan, StringComparison.OrdinalIgnoreCase);
        var items = aplicar ? await _itemRepo.GetByListId(id) : Enumerable.Empty<SupplyItem>();
        foreach (var item in items)
        {
            // Lo que el cliente agregó por su cuenta se queda como lo eligió
            if (item.Agregado) continue;

            // Si un útil no tiene producto en ese nivel, se usa el más cercano (igual que en el frontend).
            var tierProductId = request.Plan switch
            {
                "economico" => item.ProductEconomicoId ?? item.ProductMedioId ?? item.ProductPremiumId,
                "premium" => item.ProductPremiumId ?? item.ProductMedioId ?? item.ProductEconomicoId,
                "medio" => item.ProductMedioId ?? item.ProductEconomicoId ?? item.ProductPremiumId,
                _ => null,
            };

            if (tierProductId.HasValue)
            {
                if (item.MatchedProductId == tierProductId) continue;
                var tierProduct = await _productRepo.GetById(tierProductId.Value);
                if (tierProduct == null) continue;
                item.MatchedProductId = tierProduct.Id;
                item.NombreDetectado = tierProduct.Name;
                item.PriceAtMatch = tierProduct.BasePrice;
                await _itemRepo.Update(item);
                continue;
            }

            if (item.MatchedProductId.HasValue)
            {
                var currentProduct = await _productRepo.GetById(item.MatchedProductId.Value);
                if (currentProduct != null && currentProduct.Tier != request.Plan)
                {
                    var alternatives = await _productRepo.Search(item.NombreOriginal, null, 20);
                    var tierMatch = alternatives.FirstOrDefault(p => p.Tier == request.Plan && p.Category == currentProduct.Category)
                        ?? alternatives.FirstOrDefault(p => p.Tier == request.Plan);
                    if (tierMatch != null)
                    {
                        item.MatchedProductId = tierMatch.Id;
                        item.NombreDetectado = tierMatch.Name;
                        item.PriceAtMatch = tierMatch.BasePrice;
                        await _itemRepo.Update(item);
                    }
                }
            }
        }

        return Ok(ApiResponse<bool>.Ok(true));
    }

    /// <summary>El cliente deja un comentario sobre la lista (se agrega, no reemplaza). Público.</summary>
    [HttpPost("{id}/observaciones")]
    [AllowAnonymous]
    public async Task<IActionResult> AddObservacion(Guid id, [FromBody] AddObservacionRequest req)
    {
        if (string.IsNullOrWhiteSpace(req.Observacion) || req.Observacion.Length > 2000)
            return BadRequest(ApiResponse<bool>.Fail("INVALID", "Escribe un comentario (máximo 2000 caracteres)"));
        using var conn = HttpContext.RequestServices.GetRequiredService<IDbConnectionFactory>().CreateConnection();
        await Dapper.SqlMapper.ExecuteAsync(conn,
            "UPDATE supply_lists SET user_observaciones = COALESCE(user_observaciones, '') || E'\\n' || @Obs, updated_at = NOW() WHERE id = @Id",
            new { Id = id, Obs = $"[{DateTime.UtcNow:yyyy-MM-dd HH:mm}] {req.Observacion}" });
        return Ok(ApiResponse<bool>.Ok(true));
    }

    [HttpPost("from-text")]
    [AllowAnonymous]
    public async Task<IActionResult> CreateFromText([FromBody] CreateFromTextRequest request)
    {
        var school = await _schoolRepo.GetById(request.SchoolId);
        if (school == null) return BadRequest(ApiResponse<object>.Fail("NOT_FOUND", "Colegio no encontrado"));

        var grade = await _gradeRepo.GetById(request.GradeId);
        if (grade == null) return BadRequest(ApiResponse<object>.Fail("NOT_FOUND", "Grado no encontrado"));

        // La lista es de quien tiene la sesión (se ignora el userId del cuerpo)
        Guid? userId = User.GetUserId();

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
        RunInBackground(listId, s => s.ProcessMatching(listId));

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
    /// <summary>Producto del catálogo elegido por el cliente al agregar un útil a su lista</summary>
    public Guid? ProductId { get; set; }
    /// <summary>true cuando lo agrega el cliente (no el admin al revisar): no cambia al elegir otra opción</summary>
    public bool PorCliente { get; set; }
}

public class CopiarListaRequest
{
    /// <summary>Se ignora: la copia es del usuario de la sesión.</summary>
    public Guid? UserId { get; set; }
    /// <summary>Opción elegida al copiar: economico, medio o premium</summary>
    public string? Plan { get; set; }
    /// <summary>true: crea otra copia aunque ya exista una (mellizos/hermanos del mismo grado)</summary>
    public bool? Nueva { get; set; }
}

public class AddObservacionRequest
{
    public string Observacion { get; set; } = "";
}