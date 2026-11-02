using UtilesApi.Core.Entities;
using UtilesApi.Infrastructure.Database;
using UtilesApi.Infrastructure.AI;
using UtilesApi.Infrastructure.OCR;

namespace UtilesApi.Services;

public interface IProductMatchingService
{
    Task<Product?> FindBestMatch(string itemName, string? preferredTier = "medio");
    Task<List<(Product Product, int Score)>> FindMultipleMatches(string itemName, int limit = 5, string? category = null);
}

public class ProductMatchingService : IProductMatchingService
{
    private readonly ProductRepository _productRepo;

    public ProductMatchingService(ProductRepository productRepo)
    {
        _productRepo = productRepo;
    }

    public async Task<Product?> FindBestMatch(string itemName, string? preferredTier = "medio")
    {
        var products = await _productRepo.GetAll();
        var itemWords = Tokenize(itemName);

        var scored = new List<(Product product, int score)>();

        foreach (var product in products)
        {
            var productWords = Tokenize(product.Name);
            var score = ScoreMatch(itemWords, productWords);

            // Bonus for matching tier
            if (product.Tier == preferredTier) score += 10;

            if (score > 20) scored.Add((product, score));
        }

        return scored.OrderByDescending(s => s.score).FirstOrDefault().product;
    }

    public async Task<List<(Product Product, int Score)>> FindMultipleMatches(string itemName, int limit = 5, string? category = null)
    {
        var products = await _productRepo.GetAll(category);
        var itemWords = Tokenize(itemName);

        var result = new List<(Product Product, int Score)>();

        foreach (var product in products)
        {
            var score = ScoreMatch(itemWords, Tokenize(product.Name));
            if (score > 10) result.Add((product, score));
        }

        return result.OrderByDescending(r => r.Score).Take(limit).ToList();
    }

    private int ScoreMatch(string[] itemWords, string[] productWords)
    {
        int score = 0;
        int matched = 0;

        foreach (var iw in itemWords)
        {
            if (iw.Length < 2) continue;

            foreach (var pw in productWords)
            {
                if (pw.Length < 2) continue;

                // Exact match
                if (iw == pw) { score += 30; matched++; break; }

                // Contained
                if (pw.Contains(iw) || iw.Contains(pw))
                {
                    score += 20;
                    matched++;
                    break;
                }

                // Close Levenshtein
                if (iw.Length > 3 && pw.Length > 3 && LevenshteinDistance(iw, pw) <= 2)
                {
                    score += 15;
                    matched++;
                    break;
                }
            }
        }

        // Penalize if few words matched
        if (itemWords.Length > 0)
        {
            var matchRatio = (double)matched / itemWords.Length;
            score = (int)(score * matchRatio);
        }

        return score;
    }

    private string[] Tokenize(string text)
    {
        return text.ToLower()
            .Replace("-", " ")
            .Replace("(", " ")
            .Replace(")", " ")
            .Replace(",", " ")
            .Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Where(w => w.Length >= 2)
            .ToArray();
    }

    private int LevenshteinDistance(string s1, string s2)
    {
        var m = s1.Length;
        var n = s2.Length;
        var d = new int[m + 1, n + 1];
        for (var i = 0; i <= m; i++) d[i, 0] = i;
        for (var j = 0; j <= n; j++) d[0, j] = j;
        for (var i = 1; i <= m; i++)
            for (var j = 1; j <= n; j++)
            {
                var cost = s1[i - 1] == s2[j - 1] ? 0 : 1;
                d[i, j] = Math.Min(Math.Min(d[i - 1, j] + 1, d[i, j - 1] + 1), d[i - 1, j - 1] + cost);
            }
        return d[m, n];
    }
}

public class ListProcessingService
{
    private readonly SupplyListRepository _listRepo;
    private readonly SupplyItemRepository _itemRepo;
    private readonly SchoolRepository _schoolRepo;
    private readonly GradeRepository _gradeRepo;
    private readonly ProductRepository _productRepo;
    private readonly IOcrService _ocrService;
    private readonly IProductMatchingService _matchingService;
    private readonly ClaudeListReader _aiReader;
    private readonly IConfiguration _config;
    private readonly ILogger<ListProcessingService> _logger;

    public ListProcessingService(
        SupplyListRepository listRepo,
        SupplyItemRepository itemRepo,
        SchoolRepository schoolRepo,
        GradeRepository gradeRepo,
        ProductRepository productRepo,
        IOcrService ocrService,
        IProductMatchingService matchingService,
        ClaudeListReader aiReader,
        IConfiguration config,
        ILogger<ListProcessingService> logger)
    {
        _listRepo = listRepo;
        _itemRepo = itemRepo;
        _schoolRepo = schoolRepo;
        _gradeRepo = gradeRepo;
        _productRepo = productRepo;
        _ocrService = ocrService;
        _matchingService = matchingService;
        _aiReader = aiReader;
        _config = config;
        _logger = logger;
    }

    /// <summary>
    /// Lee la lista subida y crea sus útiles. Con IA configurada usa Claude; si no, usa el lector
    /// de prueba (OCR:Provider = "mock") o deja la lista para revisión manual.
    /// </summary>
    public async Task ProcessList(Guid listId)
    {
        var list = await _listRepo.GetById(listId);
        if (list == null) return;

        if (_aiReader.Enabled)
        {
            try
            {
                await ProcessWithAi(list);
            }
            catch (AiReadException ex)
            {
                await _listRepo.SetIaEstado(listId, "ERROR", ex.Message);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error leyendo la lista {ListId} con IA", listId);
                await _listRepo.SetIaEstado(listId, "ERROR", "No pudimos leer tu lista automáticamente. Nuestro equipo la revisará y te avisaremos.");
            }
            return;
        }

        if (!string.Equals(_config["OCR:Provider"], "mock", StringComparison.OrdinalIgnoreCase))
        {
            await _listRepo.SetIaEstado(listId, "MANUAL", "Nuestro equipo revisará tu lista y te avisaremos cuando esté lista.");
            return;
        }

        await ProcessWithMock(list);
        await _listRepo.SetIaEstado(listId, "DEMO", "Lista de ejemplo: la lectura con IA no está configurada en el servidor.");
    }

    private async Task ProcessWithAi(SupplyList list)
    {
        await _listRepo.SetIaEstado(list.Id, "PROCESANDO");

        var catalog = (await _productRepo.GetAll(limit: 5000)).ToList();
        if (catalog.Count == 0)
            throw new AiReadException("El catálogo está vacío; no hay productos para armar la lista.");

        var school = await _schoolRepo.GetById(list.SchoolId);
        var grade = await _gradeRepo.GetById(list.GradeId);
        var files = (list.ImageUrl ?? string.Empty).Split('|', StringSplitOptions.RemoveEmptyEntries);

        var result = await _aiReader.ReadAsync(files, catalog, school?.Name, grade?.Name);

        if (!result.Legible || result.Items.Count == 0)
            throw new AiReadException(result.Observaciones
                ?? "No encontramos útiles en el archivo. Toma la foto de frente, con buena luz y que se vea toda la hoja.");

        // Reprocesar reemplaza los útiles anteriores
        foreach (var old in await _itemRepo.GetByListId(list.Id))
            await _itemRepo.Delete(old.Id);

        // Guardar solo lo leído: la lectura puede tardar y el admin pudo cambiar el estado mientras tanto.
        await _listRepo.UpdateOcr(list.Id, result.Transcripcion, result.Colegio, result.Grado);

        var plan = (await _listRepo.GetById(list.Id))?.Plan ?? list.Plan ?? "medio";
        foreach (var it in result.Items)
        {
            var elegido = plan switch
            {
                "economico" => it.Economico ?? it.Medio ?? it.Premium,
                "premium" => it.Premium ?? it.Medio ?? it.Economico,
                _ => it.Medio ?? it.Economico ?? it.Premium,
            };

            var item = new SupplyItem
            {
                SupplyListId = list.Id,
                NombreOriginal = it.TextoOriginal,
                NombreDetectado = elegido?.Name ?? it.Nombre,
                Cantidad = it.Cantidad,
                Notas = it.Notas,
                MatchedProductId = elegido?.Id,
                MatchedQuantity = it.Cantidad,
                PriceAtMatch = elegido?.BasePrice,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow,
            };
            item.Id = await _itemRepo.Create(item);

            item.ProductEconomicoId = it.Economico?.Id;
            item.PriceEconomico = it.Economico?.BasePrice;
            item.ProductMedioId = it.Medio?.Id;
            item.PriceMedio = it.Medio?.BasePrice;
            item.ProductPremiumId = it.Premium?.Id;
            item.PricePremium = it.Premium?.BasePrice;
            item.Forro = it.Forrado;
            item.ForroColor = it.ColorForro;
            await _itemRepo.Update(item);
        }

        var sinProducto = result.Items.Count(i => i.Economico == null && i.Medio == null && i.Premium == null);
        var mensaje = result.Observaciones;
        if (sinProducto > 0)
        {
            var aviso = sinProducto == 1
                ? "1 útil no está en nuestro catálogo; nuestro equipo lo revisará."
                : $"{sinProducto} útiles no están en nuestro catálogo; nuestro equipo los revisará.";
            mensaje = string.IsNullOrWhiteSpace(mensaje) ? aviso : $"{mensaje} {aviso}";
        }
        await _listRepo.SetIaEstado(list.Id, "LISTO", mensaje);
    }

    private async Task ProcessWithMock(SupplyList list)
    {
        var listId = list.Id;
        var ocrResult = await _ocrService.ExtractTextAsync(list.ImageUrl ?? "");

        list.OcrText = ocrResult.RawText;
        if (ocrResult.ParsedData != null)
        {
            list.ParsedCollege = ocrResult.ParsedData.College;
            list.ParsedGrade = ocrResult.ParsedData.Grade;
        }

        await _listRepo.Update(list);

        var preferredTier = list.Plan ?? "medio";

        if (ocrResult.ParsedData != null)
        {
            foreach (var item in ocrResult.ParsedData.Items)
            {
                var supplyItem = new SupplyItem
                {
                    Id = Guid.NewGuid(),
                    SupplyListId = listId,
                    NombreOriginal = item.Name,
                    NombreDetectado = item.Name,
                    Cantidad = item.Quantity,
                    Notas = item.Notes,
                    CreatedAt = DateTime.UtcNow,
                    UpdatedAt = DateTime.UtcNow
                };

                var matchedProduct = await _matchingService.FindBestMatch(item.Name, preferredTier);
                if (matchedProduct != null)
                {
                    supplyItem.MatchedProductId = matchedProduct.Id;
                    supplyItem.NombreDetectado = matchedProduct.Name;
                    supplyItem.MatchedQuantity = item.Quantity;
                    supplyItem.PriceAtMatch = matchedProduct.BasePrice;
                }

                await _itemRepo.Create(supplyItem);
            }
        }
    }

    public async Task ProcessMatching(Guid listId)
    {
        var list = await _listRepo.GetById(listId);
        var preferredTier = list?.Plan ?? "medio";
        var items = await _itemRepo.GetByListId(listId);

        foreach (var item in items)
        {
            var changed = false;

            // Auto-match default product
            if (item.MatchedProductId == null)
            {
                var matchedProduct = await _matchingService.FindBestMatch(item.NombreOriginal, preferredTier);
                if (matchedProduct != null)
                {
                    item.MatchedProductId = matchedProduct.Id;
                    item.NombreDetectado = matchedProduct.Name;
                    item.MatchedQuantity = item.Cantidad;
                    item.PriceAtMatch = matchedProduct.BasePrice;
                    changed = true;
                }
            }

            // Auto-match per tier
            if (item.ProductEconomicoId == null)
            {
                var p = await _matchingService.FindBestMatch(item.NombreOriginal, "economico");
                if (p != null) { item.ProductEconomicoId = p.Id; item.PriceEconomico = p.BasePrice; changed = true; }
            }
            if (item.ProductMedioId == null)
            {
                var p = await _matchingService.FindBestMatch(item.NombreOriginal, "medio");
                if (p != null) { item.ProductMedioId = p.Id; item.PriceMedio = p.BasePrice; changed = true; }
            }
            if (item.ProductPremiumId == null)
            {
                var p = await _matchingService.FindBestMatch(item.NombreOriginal, "premium");
                if (p != null) { item.ProductPremiumId = p.Id; item.PricePremium = p.BasePrice; changed = true; }
            }

            if (changed) await _itemRepo.Update(item);
        }
    }
}
