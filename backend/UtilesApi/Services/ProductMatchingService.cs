using UtilesApi.Core.Entities;
using UtilesApi.Infrastructure.Database;
using UtilesApi.Infrastructure.OCR;

namespace UtilesApi.Services;

public interface IProductMatchingService
{
    Task<Product?> FindBestMatch(string itemName, string? category = null);
    Task<List<(Product Product, int Score)>> FindMultipleMatches(string itemName, int limit = 5, string? category = null);
}

public class ProductMatchingService : IProductMatchingService
{
    private readonly ProductRepository _productRepo;

    public ProductMatchingService(ProductRepository productRepo)
    {
        _productRepo = productRepo;
    }

    public async Task<Product?> FindBestMatch(string itemName, string? category = null)
    {
        var matches = await FindMultipleMatches(itemName, 5, category);
        return matches.FirstOrDefault().Product;
    }

    public async Task<List<(Product Product, int Score)>> FindMultipleMatches(string itemName, int limit = 5, string? category = null)
    {
        var products = await _productRepo.GetAll(category);
        var result = new List<(Product Product, int Score)>();

        var normalizedItemName = Normalize(itemName);

        foreach (var product in products)
        {
            var score = CalculateSimilarity(normalizedItemName, Normalize(product.Name));
            
            if (score > 0)
            {
                result.Add((product, score));
            }
        }

        return result
            .OrderByDescending(r => r.Score)
            .Take(limit)
            .ToList();
    }

    private int CalculateSimilarity(string s1, string s2)
    {
        if (s1 == s2) return 100;
        
        var words1 = s1.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var words2 = s2.Split(' ', StringSplitOptions.RemoveEmptyEntries);

        int matchedWords = 0;
        int totalWords = Math.Max(words1.Length, words2.Length);

        foreach (var word1 in words1)
        {
            foreach (var word2 in words2)
            {
                if (LevenshteinDistance(word1, word2) <= 2)
                {
                    matchedWords++;
                    break;
                }
                if (word2.Contains(word1) || word1.Contains(word2))
                {
                    matchedWords++;
                    break;
                }
            }
        }

        var wordScore = totalWords > 0 ? (matchedWords * 100) / totalWords : 0;

        var lengthPenalty = Math.Abs(s1.Length - s2.Length) * 2;
        var finalScore = Math.Max(0, wordScore - lengthPenalty);

        return finalScore;
    }

    private string Normalize(string text)
    {
        return text.ToLower()
            .Replace("cuaderno", "")
            .Replace("lapiz", "")
            .Replace("lapices", "")
            .Replace("colores", "")
            .Replace("grafito", "")
            .Replace("gom", "")
            .Replace("regla", "")
            .Replace("pegamento", "")
            .Replace("block", "")
            .Replace("cuadriculado", "")
            .Replace("college", "")
            .Replace("7mm", "")
            .Replace("mm", "")
            .Replace("cm", "")
            .Replace("-", " ")
            .Replace("(", " ")
            .Replace(")", " ")
            .Trim();
    }

    private int LevenshteinDistance(string s1, string s2)
    {
        var m = s1.Length;
        var n = s2.Length;
        var d = new int[m + 1, n + 1];

        for (var i = 0; i <= m; i++) d[i, 0] = i;
        for (var j = 0; j <= n; j++) d[0, j] = j;

        for (var i = 1; i <= m; i++)
        {
            for (var j = 1; j <= n; j++)
            {
                var cost = s1[i - 1] == s2[j - 1] ? 0 : 1;
                d[i, j] = Math.Min(
                    Math.Min(d[i - 1, j] + 1, d[i, j - 1] + 1),
                    d[i - 1, j - 1] + cost);
            }
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

                var matchedProduct = await _matchingService.FindBestMatch(item.Name);
                if (matchedProduct != null)
                {
                    supplyItem.MatchedProductId = matchedProduct.Id;
                    supplyItem.MatchedQuantity = item.Quantity;
                    supplyItem.PriceAtMatch = matchedProduct.BasePrice;
                }

                await _itemRepo.Create(supplyItem);
            }
        }
    }

    public async Task ProcessMatching(Guid listId)
    {
        var items = await _itemRepo.GetByListId(listId);
        
        foreach (var item in items)
        {
            if (item.MatchedProductId == null)
            {
                var matchedProduct = await _matchingService.FindBestMatch(item.NombreOriginal);
                if (matchedProduct != null)
                {
                    item.MatchedProductId = matchedProduct.Id;
                    item.MatchedQuantity = item.Cantidad;
                    item.PriceAtMatch = matchedProduct.BasePrice;
                    await _itemRepo.Update(item);
                }
            }
        }
    }
}