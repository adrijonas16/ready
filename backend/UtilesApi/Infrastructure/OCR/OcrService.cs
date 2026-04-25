using System.Text.RegularExpressions;

namespace UtilesApi.Infrastructure.OCR;

public interface IOcrService
{
    Task<OcrResult> ExtractTextAsync(string imageUrl);
}

public class OcrResult
{
    public string RawText { get; set; } = string.Empty;
    public ParsedListData? ParsedData { get; set; }
}

public class ParsedListData
{
    public string? College { get; set; }
    public string? Grade { get; set; }
    public List<ParsedItem> Items { get; set; } = new();
}

public class ParsedItem
{
    public string Name { get; set; } = string.Empty;
    public int Quantity { get; set; } = 1;
    public string? Notes { get; set; }
}

public class AzureOcrService : IOcrService
{
    private readonly HttpClient _httpClient;
    private readonly string _endpoint;
    private readonly string _apiKey;

    public AzureOcrService(HttpClient httpClient, IConfiguration configuration)
    {
        _httpClient = httpClient;
        _endpoint = configuration["Azure:Ocr:Endpoint"] ?? "";
        _apiKey = configuration["Azure:Ocr:ApiKey"] ?? "";
    }

    public async Task<OcrResult> ExtractTextAsync(string imageUrl)
    {
        // Simulated OCR - in production, call Azure Computer Vision API
        // POST {endpoint}/vision/v3.2/read/analyze
        
        var result = new OcrResult
        {
            RawText = "COLEGIO SANTA MARÍA\n1° BÁSICO 2024\n\n- Cuaderno college 7mm (3 unidades)\n- Lápices de colores (12 colores)\n- Goma de borrar\n- Regla 30cm\n- Forrar cuadernos\n- Pegamento en barra",
            ParsedData = new ParsedListData
            {
                College = "Colegio Santa María",
                Grade = "1° Básico",
                Items = new List<ParsedItem>
                {
                    new() { Name = "Cuaderno college 7mm", Quantity = 3, Notes = null },
                    new() { Name = "Lápices de colores 12 colores", Quantity = 1, Notes = null },
                    new() { Name = "Goma de borrar", Quantity = 1, Notes = null },
                    new() { Name = "Regla 30cm", Quantity = 1, Notes = null },
                    new() { Name = "Pegamento en barra", Quantity = 1, Notes = "Forrar cuadernos" }
                }
            }
        };

        return await Task.FromResult(result);
    }
}

public class MockOcrService : IOcrService
{
    public async Task<OcrResult> ExtractTextAsync(string imageUrl)
    {
        // Return mock data for development
        return await Task.FromResult(new OcrResult
        {
            RawText = "Lista de útiles escolares",
            ParsedData = new ParsedListData
            {
                Items = new List<ParsedItem>
                {
                    new() { Name = "Cuaderno", Quantity = 3 },
                    new() { Name = "Lápiz", Quantity = 6 },
                    new() { Name = "Goma", Quantity = 1 }
                }
            }
        });
    }
}

public class ListParserService
{
    private static readonly string[] QuantityPatterns = 
    {
        @"\((\d+)\s*(?:unidades?|unds?|unds|piezas?|pcs?)\)",
        @"(\d+)\s*(?:unidades?|unds?|unds|piezas?|pcs?)",
        @"^\s*(\d+)\s+"
    };

    public ParsedListData Parse(string rawText)
    {
        var lines = rawText.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(l => l.Trim())
            .Where(l => !string.IsNullOrWhiteSpace(l))
            .ToList();

        var data = new ParsedListData();

        // Try to extract college name
        var collegeLine = lines.FirstOrDefault(l => 
            l.Contains("colegio", StringComparison.OrdinalIgnoreCase) ||
            l.Contains("escuela", StringComparison.OrdinalIgnoreCase));
        
        if (collegeLine != null)
        {
            data.College = ExtractValue(collegeLine);
        }

        // Try to extract grade
        var gradeLine = lines.FirstOrDefault(l => 
            Regex.IsMatch(l, @"\d+\s*°?\s*(?:básico|medio|preuniversitario)", RegexOptions.IgnoreCase));
        
        if (gradeLine != null)
        {
            data.Grade = ExtractValue(gradeLine);
        }

        // Parse items
        var itemLines = lines.Where(l => 
            !l.Contains("colegio", StringComparison.OrdinalIgnoreCase) &&
            !l.Contains("escuela", StringComparison.OrdinalIgnoreCase) &&
            !Regex.IsMatch(l, @"^\d+\s*°", RegexOptions.IgnoreCase) &&
            l.Length > 3 &&
            (l.StartsWith("-") || l.StartsWith("•") || l.StartsWith("*") || char.IsDigit(l[0]))).ToList();

        foreach (var line in itemLines)
        {
            var item = ParseItemLine(line);
            if (item != null)
            {
                data.Items.Add(item);
            }
        }

        return data;
    }

    private string ExtractValue(string line)
    {
        var parts = line.Split(':');
        if (parts.Length > 1)
            return parts[1].Trim();
        
        parts = line.Split(new[] { "colegio", "escuela", "gracias", "grado" }, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length > 1)
            return parts[1].Trim();
        
        return line.TrimStart('-', '•', '*', ' ', '\t');
    }

    private ParsedItem? ParseItemLine(string line)
    {
        line = line.TrimStart('-', '•', '*', ' ', '\t');
        
        if (line.Length < 2) return null;

        var quantity = 1;
        var notes = (string?)null;

        foreach (var pattern in QuantityPatterns)
        {
            var match = Regex.Match(line, pattern, RegexOptions.IgnoreCase);
            if (match.Success)
            {
                quantity = int.Parse(match.Groups[1].Value);
                line = line.Replace(match.Value, "").Trim();
                break;
            }
        }

        // Check for notes keywords
        var notesKeywords = new[] { "forrar", "nombre", "etiqueta", "personalizar" };
        foreach (var keyword in notesKeywords)
        {
            if (line.Contains(keyword, StringComparison.OrdinalIgnoreCase))
            {
                notes = keyword;
                break;
            }
        }

        return new ParsedItem
        {
            Name = line.Trim(),
            Quantity = quantity,
            Notes = notes
        };
    }
}