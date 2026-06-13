using UtilesApi.Core.Entities;
using UtilesApi.Infrastructure.Database;
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
    private readonly IOcrService _ocrService;
    private readonly IProductMatchingService _matchingService;

    public ListProcessingService(
        SupplyListRepository listRepo,
        SupplyItemRepository itemRepo,
        IOcrService ocrService,
        IProductMatchingService matchingService)
    {
        _listRepo = listRepo;
        _itemRepo = itemRepo;
        _ocrService = ocrService;
        _matchingService = matchingService;
    }

    public async Task ProcessList(Guid listId)
    {
        var list = await _listRepo.GetById(listId);
        if (list == null) return;

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
