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
                UserObservaciones = list.UserObservaciones,
                IaEstado = list.IaEstado,
                IaMensaje = list.IaMensaje,
                Extras = list.Extras,
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
                IaEstado = list.IaEstado,
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
            RunInBackground(id, s => s.ProcessMatching(id));

            // Aviso al cliente: ya puede elegir su opción y comprar
            var aprobada = await _listRepo.GetById(id);
            if (aprobada?.UserId != null)
            {
                try { await _notifications.NotifyListReady(aprobada.UserId.Value.ToString(), id.ToString()); }
                catch (Exception ex) { _logger.LogWarning(ex, "No se pudo notificar la aprobación de la lista {ListId}", id); }
            }
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
    public async Task<ActionResult<ApiResponse<bool>>> UpdateExtras(Guid id, [FromBody] System.Text.Json.JsonElement extras)
    {
        var list = await _listRepo.GetById(id);
        if (list == null) return NotFound(ApiResponse<bool>.Fail("NOT_FOUND", "Lista no encontrada"));
        if (extras.ValueKind != System.Text.Json.JsonValueKind.Object)
            return BadRequest(ApiResponse<bool>.Fail("INVALID", "Formato de extras inválido"));

        var json = extras.GetRawText();
        if (json.Length > 20000)
            return BadRequest(ApiResponse<bool>.Fail("TOO_LARGE", "Demasiados datos en los extras"));

        await _listRepo.UpdateExtras(id, json);
        return Ok(ApiResponse<bool>.Ok(true));
    }

    [HttpPost("{id}/items")]
    public async Task<ActionResult<ApiResponse<SupplyItemResponse>>> AddItem(Guid id, [FromBody] CreateFromTextItem request)
    {
        var list = await _listRepo.GetById(id);
        if (list == null) return NotFound(ApiResponse<SupplyItemResponse>.Fail("NOT_FOUND", "Lista no encontrada"));
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
    public async Task<ActionResult<ApiResponse<bool>>> DeleteItem(Guid id, Guid itemId)
    {
        var items = await _itemRepo.GetByListId(id);
        if (!items.Any(i => i.Id == itemId))
            return NotFound(ApiResponse<bool>.Fail("ITEM_NOT_FOUND", "Item no encontrado"));
        await _itemRepo.Delete(itemId);
        return Ok(ApiResponse<bool>.Ok(true));
    }

    /// <summary>
    /// Copia personal de una lista (normalmente la oficial del colegio) para que cada familia
    /// cambie productos, cantidades y extras sin tocar la lista que ven los demás.
    /// Si el usuario ya tiene una copia de esa lista, devuelve la misma.
    /// </summary>
    [HttpPost("{id}/copia")]
    public async Task<ActionResult<ApiResponse<object>>> Copiar(Guid id, [FromBody] CopiarListaRequest request)
    {
        var list = await _listRepo.GetById(id);
        if (list == null) return NotFound(ApiResponse<object>.Fail("NOT_FOUND", "Lista no encontrada"));

        using var conn = HttpContext.RequestServices.GetRequiredService<IDbConnectionFactory>().CreateConnection();
        if (request.UserId.HasValue)
        {
            var existente = await Dapper.SqlMapper.ExecuteScalarAsync<Guid?>(conn,
                "SELECT id FROM supply_lists WHERE copia_de = @Id AND user_id = @UserId ORDER BY created_at DESC LIMIT 1",
                new { Id = id, request.UserId });
            if (existente.HasValue) return Ok(ApiResponse<object>.Ok(new { id = existente.Value, existente = true }));
        }

        var nuevaId = await Dapper.SqlMapper.ExecuteScalarAsync<Guid>(conn, @"
            INSERT INTO supply_lists (user_id, school_id, grade_id, year, image_url, ocr_text, estado, es_oficial, submitted_by,
                                      fecha_subida, fecha_validacion, ia_estado, extras, copia_de)
            SELECT @UserId, school_id, grade_id, year, image_url, ocr_text, 'VALIDADA', FALSE, submitted_by,
                   NOW(), NOW(), ia_estado, extras, id
            FROM supply_lists WHERE id = @Id
            RETURNING id", new { Id = id, request.UserId });

        await Dapper.SqlMapper.ExecuteAsync(conn, @"
            INSERT INTO supply_items (supply_list_id, product_id, nombre_original, nombre_detectado, cantidad, notas,
                matched_product_id, matched_quantity, price_at_match, product_economico_id, product_medio_id, product_premium_id,
                price_economico, price_medio, price_premium, created_at)
            SELECT @Nueva, product_id, nombre_original, nombre_detectado, cantidad, notas,
                matched_product_id, matched_quantity, price_at_match, product_economico_id, product_medio_id, product_premium_id,
                price_economico, price_medio, price_premium, created_at
            FROM supply_items WHERE supply_list_id = @Id", new { Id = id, Nueva = nuevaId });

        if (!string.IsNullOrWhiteSpace(request.Plan))
            await UpdatePlan(nuevaId, new UpdateListPlanRequest { Plan = request.Plan, AplicarATodos = true });

        return Ok(ApiResponse<object>.Ok(new { id = nuevaId, existente = false }));
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
    public Guid? UserId { get; set; }
    /// <summary>Opción elegida al copiar: economico, medio o premium</summary>
    public string? Plan { get; set; }
}

public class AddObservacionRequest
{
    public string Observacion { get; set; } = "";
}