using System.IO.Compression;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using UtilesApi.Core.Entities;

namespace UtilesApi.Infrastructure.AI;

/// <summary>
/// Configuración de la IA. Se lee de la sección "AI" de appsettings o de variables de entorno
/// (AI__ApiKey, o ANTHROPIC_API_KEY).
/// </summary>
public class AiOptions
{
    public string ApiKey { get; set; } = string.Empty;
    public string Model { get; set; } = "claude-sonnet-5-5";
    public string BaseUrl { get; set; } = "https://api.anthropic.com";
    public int MaxTokens { get; set; } = 12000;

    public bool Enabled => !string.IsNullOrWhiteSpace(ApiKey);
}

public class AiListResult
{
    public bool Legible { get; set; } = true;
    public string? Colegio { get; set; }
    public string? Grado { get; set; }
    public string? Transcripcion { get; set; }
    public string? Observaciones { get; set; }
    public List<AiListItem> Items { get; set; } = new();
    public List<AiBook> Libros { get; set; } = new();
    /// <summary>La lista pide forrar los cuadernos.</summary>
    public bool ForrarCuadernos { get; set; }
    public string? ColorForro { get; set; }
    /// <summary>La lista pide útiles marcados con nombre / etiquetados.</summary>
    public bool Etiquetar { get; set; }
}

public class AiBook
{
    public string Titulo { get; set; } = string.Empty;
    public string? Editorial { get; set; }
    public int Cantidad { get; set; } = 1;
    public Product? Producto { get; set; }
}

public class AiListItem
{
    public string TextoOriginal { get; set; } = string.Empty;
    public string Nombre { get; set; } = string.Empty;
    public int Cantidad { get; set; } = 1;
    public string? Notas { get; set; }
    public bool Forrado { get; set; }
    public string? ColorForro { get; set; }
    public Product? Economico { get; set; }
    public Product? Medio { get; set; }
    public Product? Premium { get; set; }
}

public class AiReadException : Exception
{
    public AiReadException(string message) : base(message) { }
}

/// <summary>
/// Lee la lista de útiles (fotos, PDF o Word) con Claude y, en la misma pasada,
/// elige del catálogo la opción económica, estándar y premium de cada útil.
/// </summary>
public class ClaudeListReader
{
    private const string ToolName = "registrar_lista";
    private const long MaxImageBytes = 5 * 1024 * 1024;
    private static readonly string[] ImageTypes = { "image/jpeg", "image/png", "image/webp", "image/gif" };

    private readonly HttpClient _http;
    private readonly AiOptions _options;
    private readonly IConfiguration _config;
    private readonly ILogger<ClaudeListReader> _logger;

    public ClaudeListReader(HttpClient http, AiOptions options, IConfiguration config, ILogger<ClaudeListReader> logger)
    {
        _http = http;
        _options = options;
        _config = config;
        _logger = logger;
    }

    public bool Enabled => _options.Enabled;

    public async Task<AiListResult> ReadAsync(IEnumerable<string> fileUrls, IReadOnlyList<Product> catalog, string? schoolHint, string? gradeHint, CancellationToken ct = default)
    {
        var content = new JsonArray();
        var skipped = new List<string>();

        foreach (var url in fileUrls.Where(u => !string.IsNullOrWhiteSpace(u)))
        {
            var (bytes, mediaType) = await DownloadAsync(url, ct);
            if (bytes is null) { skipped.Add(Path.GetFileName(url)); continue; }

            if (ImageTypes.Contains(mediaType))
            {
                if (bytes.Length > MaxImageBytes) { skipped.Add($"{Path.GetFileName(url)} (más de 5 MB)"); continue; }
                content.Add(new JsonObject
                {
                    ["type"] = "image",
                    ["source"] = new JsonObject { ["type"] = "base64", ["media_type"] = mediaType, ["data"] = Convert.ToBase64String(bytes) }
                });
            }
            else if (mediaType == "application/pdf")
            {
                content.Add(new JsonObject
                {
                    ["type"] = "document",
                    ["source"] = new JsonObject { ["type"] = "base64", ["media_type"] = "application/pdf", ["data"] = Convert.ToBase64String(bytes) }
                });
            }
            else if (mediaType.Contains("wordprocessingml") || url.EndsWith(".docx", StringComparison.OrdinalIgnoreCase))
            {
                var text = ExtractDocxText(bytes);
                if (string.IsNullOrWhiteSpace(text)) { skipped.Add(Path.GetFileName(url)); continue; }
                content.Add(new JsonObject { ["type"] = "text", ["text"] = $"CONTENIDO DEL ARCHIVO WORD:\n{text}" });
            }
            else
            {
                skipped.Add(Path.GetFileName(url));
            }
        }

        if (content.Count == 0)
            throw new AiReadException("No pudimos abrir los archivos. Sube la lista como foto (JPG o PNG), PDF o Word .docx.");

        // Los ids del catálogo se reemplazan por números cortos para ahorrar tokens.
        var indexed = catalog
            .Where(p => !string.Equals(p.Category, "Servicios", StringComparison.OrdinalIgnoreCase))
            .Select((p, i) => (Index: i + 1, Product: p)).ToList();
        var byIndex = indexed.ToDictionary(x => x.Index, x => x.Product);

        var catalogText = new StringBuilder();
        catalogText.AppendLine("n | producto | categoría | marca | nivel | precio S/ | se vende por");
        foreach (var (index, p) in indexed.OrderBy(x => x.Product.Category).ThenBy(x => x.Product.Name))
        {
            var unidad = p.UnitQuantity > 1 ? $"{p.SaleUnit} x{p.UnitQuantity}" : p.SaleUnit;
            catalogText.AppendLine($"{index} | {p.Name} | {p.Category} | {p.Brand ?? "-"} | {p.Tier} | {p.BasePrice:0.00} | {unidad}{(p.Stock <= 0 ? " | AGOTADO" : "")}");
        }

        var prompt = new StringBuilder();
        prompt.AppendLine("CATÁLOGO DE LA TIENDA:");
        prompt.AppendLine(catalogText.ToString());
        if (!string.IsNullOrWhiteSpace(schoolHint)) prompt.AppendLine($"Colegio seleccionado por el cliente: {schoolHint}");
        if (!string.IsNullOrWhiteSpace(gradeHint)) prompt.AppendLine($"Grado seleccionado por el cliente: {gradeHint}");
        prompt.AppendLine("Lee la lista de útiles de los archivos adjuntos y regístrala con la herramienta.");
        content.Add(new JsonObject { ["type"] = "text", ["text"] = prompt.ToString() });

        var body = new JsonObject
        {
            ["model"] = _options.Model,
            ["max_tokens"] = _options.MaxTokens,
            ["system"] = SystemPrompt,
            ["tools"] = new JsonArray { ToolSchema() },
            ["tool_choice"] = new JsonObject { ["type"] = "tool", ["name"] = ToolName },
            ["messages"] = new JsonArray { new JsonObject { ["role"] = "user", ["content"] = content } }
        };

        var input = await CallAsync(body, ct);
        var result = Map(input, byIndex);

        if (skipped.Count > 0)
        {
            var aviso = $"No pudimos leer: {string.Join(", ", skipped)}.";
            result.Observaciones = string.IsNullOrWhiteSpace(result.Observaciones) ? aviso : $"{result.Observaciones} {aviso}";
        }
        return result;
    }

    private const string SystemPrompt = """
        Eres el asistente de Ready, una tienda de útiles escolares en Arequipa, Perú.
        Recibes la lista de útiles que pidió un colegio (fotos, PDF o texto) y la conviertes en un pedido
        usando SOLO productos del catálogo que se te entrega.

        Cómo registrar cada útil:
        - Registra cada útil una sola vez y en el orden de la lista. No inventes útiles que no estén escritos.
          Si la lista separa por cursos y repite un útil, junta las cantidades en un solo registro y menciónalo en "notas".
        - "textoOriginal": la línea tal como está escrita (corrige solo errores evidentes de lectura).
        - "nombre": nombre corto y claro del útil, por ejemplo "Cuaderno A4 cuadriculado 100 hojas".
        - "cantidad": cuántas unidades del producto del catálogo hay que comprar para cubrir lo que pide la lista.
          Ten en cuenta "se vende por": si piden 200 hojas bond y el producto es un paquete x100, la cantidad es 2;
          si piden 1 docena de lápices y el producto es una caja x12, la cantidad es 1.
        - "economico", "medio" y "premium": el número (columna n) del producto del catálogo que mejor cumple el útil en
          ese nivel de precio. Respeta lo que pide la lista (tamaño, hojas, rayado, cantidad de colores, marca si la exige).
          Si no hay un producto de ese nivel, usa el más cercano en precio que sí cumpla. Si nada del catálogo sirve, usa null.
          Evita productos AGOTADO si hay otra opción.
        - "notas": indicaciones del colegio para ese útil (marca exigida, color, "con nombre", "forrado", curso al que va).
        - "forrado": true si la lista pide forrar ese útil; "colorForro" si indica color.

        Libros y extras de toda la lista:
        - Los libros, textos escolares, planes lectores y cuadernos de trabajo de editorial van en "libros"
          (no en "items"): "titulo", "editorial" si aparece, "cantidad" y "producto" con el número del catálogo
          si ese mismo libro está en el catálogo (si no, null).
        - "forrarCuadernos": true si la lista pide forrar los cuadernos en general; "colorForroGeneral" si indica color.
        - "etiquetar": true si pide marcar los útiles con nombre o ponerles etiqueta.

        Vocabulario frecuente en listas peruanas: cuad. = cuaderno; cuadric./cuadriculado; rayado/una raya; doble raya;
        triple renglón; papelote/papelógrafo; papel lustre; cartulina; plumón indeleble/acrílico; témperas; micas;
        fólder/folder; vinifán (forro plástico); block (de dibujo); plastilina; silicona; limpiatipo.

        Si los archivos no son una lista de útiles o no se pueden leer, marca "legible": false y explica en
        "observaciones" cómo tomar una mejor foto. En "transcripcion" copia el texto completo de la lista.
        Responde siempre llamando a la herramienta registrar_lista.
        """;

    private static JsonObject ToolSchema() => JsonNode.Parse($$"""
        {
          "name": "{{ToolName}}",
          "description": "Registra los útiles de la lista escolar y los productos del catálogo que los cubren.",
          "input_schema": {
            "type": "object",
            "properties": {
              "legible": { "type": "boolean" },
              "colegio": { "type": "string" },
              "grado": { "type": "string" },
              "transcripcion": { "type": "string" },
              "observaciones": { "type": "string" },
              "forrarCuadernos": { "type": "boolean" },
              "colorForroGeneral": { "type": "string" },
              "etiquetar": { "type": "boolean" },
              "libros": {
                "type": "array",
                "items": {
                  "type": "object",
                  "properties": {
                    "titulo": { "type": "string" },
                    "editorial": { "type": "string" },
                    "cantidad": { "type": "integer", "minimum": 1 },
                    "producto": { "type": ["integer", "null"] }
                  },
                  "required": ["titulo"]
                }
              },
              "items": {
                "type": "array",
                "items": {
                  "type": "object",
                  "properties": {
                    "textoOriginal": { "type": "string" },
                    "nombre": { "type": "string" },
                    "cantidad": { "type": "integer", "minimum": 1 },
                    "notas": { "type": "string" },
                    "forrado": { "type": "boolean" },
                    "colorForro": { "type": "string" },
                    "economico": { "type": ["integer", "null"] },
                    "medio": { "type": ["integer", "null"] },
                    "premium": { "type": ["integer", "null"] }
                  },
                  "required": ["textoOriginal", "nombre", "cantidad"]
                }
              }
            },
            "required": ["legible", "items"]
          }
        }
        """)!.AsObject();

    private async Task<JsonNode> CallAsync(JsonObject body, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, $"{_options.BaseUrl.TrimEnd('/')}/v1/messages");
        req.Headers.Add("x-api-key", _options.ApiKey);
        req.Headers.Add("anthropic-version", "2023-06-01");
        req.Content = new StringContent(body.ToJsonString(), Encoding.UTF8);
        req.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");

        using var res = await _http.SendAsync(req, ct);
        var json = await res.Content.ReadAsStringAsync(ct);
        if (!res.IsSuccessStatusCode)
        {
            _logger.LogError("Anthropic respondió {Status}: {Body}", (int)res.StatusCode, json);
            throw new AiReadException("El servicio de lectura no respondió. Vuelve a intentarlo en unos minutos.");
        }

        var root = JsonNode.Parse(json);
        if (root?["stop_reason"]?.ToString() == "max_tokens")
        {
            _logger.LogWarning("La respuesta de la IA se cortó por max_tokens ({Max})", _options.MaxTokens);
            throw new AiReadException("La lista es muy larga para leerla de una vez. Súbela en partes (una foto por hoja) o avísanos y la armamos nosotros.");
        }

        var blocks = root?["content"] as JsonArray;
        var tool = blocks?.FirstOrDefault(b => b?["type"]?.ToString() == "tool_use" && b?["name"]?.ToString() == ToolName);
        return tool?["input"] ?? throw new AiReadException("No pudimos interpretar la lista. Prueba con una foto más nítida.");
    }

    private static AiListResult Map(JsonNode input, Dictionary<int, Product> byIndex)
    {
        Product? P(JsonNode? n) => ToInt(n) is int i && byIndex.TryGetValue(i, out var p) ? p : null;

        var result = new AiListResult
        {
            Legible = ToBool(input["legible"]) ?? true,
            Colegio = Str(input["colegio"]),
            Grado = Str(input["grado"]),
            Transcripcion = Str(input["transcripcion"]),
            Observaciones = Str(input["observaciones"]),
        };

        result.ForrarCuadernos = ToBool(input["forrarCuadernos"]) ?? false;
        result.ColorForro = Str(input["colorForroGeneral"]);
        result.Etiquetar = ToBool(input["etiquetar"]) ?? false;
        foreach (var lb in input["libros"] as JsonArray ?? new JsonArray())
        {
            var titulo = Str(lb?["titulo"]);
            if (titulo is null) continue;
            result.Libros.Add(new AiBook
            {
                Titulo = titulo,
                Editorial = Str(lb?["editorial"]),
                Cantidad = Math.Clamp(ToInt(lb?["cantidad"]) ?? 1, 1, 20),
                Producto = P(lb?["producto"]),
            });
        }

        foreach (var it in input["items"] as JsonArray ?? new JsonArray())
        {
            if (it is null) continue;
            var nombre = Str(it["nombre"]) ?? Str(it["textoOriginal"]);
            if (nombre is null) continue;
            result.Items.Add(new AiListItem
            {
                TextoOriginal = Str(it["textoOriginal"]) ?? nombre,
                Nombre = nombre,
                Cantidad = Math.Clamp(ToInt(it["cantidad"]) ?? 1, 1, 99),
                Notas = Str(it["notas"]),
                Forrado = ToBool(it["forrado"]) ?? false,
                ColorForro = Str(it["colorForro"]),
                Economico = P(it["economico"]),
                Medio = P(it["medio"]),
                Premium = P(it["premium"]),
            });
        }
        return result;
    }

    /// <summary>Descarga un archivo subido. Si el almacenamiento es local, lo lee directo del disco.</summary>
    private async Task<(byte[]? Bytes, string MediaType)> DownloadAsync(string url, CancellationToken ct)
    {
        var mediaType = "application/octet-stream";
        try
        {
            mediaType = GuessMediaType(url);
            var localBase = _config["Storage:BaseUrl"];
            var localPath = _config["Storage:LocalPath"];
            if (!string.IsNullOrEmpty(localBase) && !string.IsNullOrEmpty(localPath) && url.StartsWith(localBase, StringComparison.OrdinalIgnoreCase))
            {
                var file = Path.Combine(localPath, Path.GetFileName(new Uri(url).LocalPath));
                if (File.Exists(file)) return (await File.ReadAllBytesAsync(file, ct), mediaType);
            }

            using var res = await _http.GetAsync(url, ct);
            if (!res.IsSuccessStatusCode) return (null, mediaType);
            var header = res.Content.Headers.ContentType?.MediaType;
            if (!string.IsNullOrEmpty(header) && header != "application/octet-stream") mediaType = header.ToLowerInvariant();
            return (await res.Content.ReadAsByteArrayAsync(ct), mediaType);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "No se pudo descargar {Url}", url);
            return (null, mediaType);
        }
    }

    private static string GuessMediaType(string url)
    {
        var ext = Path.GetExtension(new Uri(url, UriKind.RelativeOrAbsolute).IsAbsoluteUri ? new Uri(url).LocalPath : url).ToLowerInvariant();
        return ext switch
        {
            ".jpg" or ".jpeg" => "image/jpeg",
            ".png" => "image/png",
            ".webp" => "image/webp",
            ".gif" => "image/gif",
            ".pdf" => "application/pdf",
            ".docx" => "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
            ".doc" => "application/msword",
            _ => "application/octet-stream",
        };
    }

    /// <summary>Texto plano de un .docx (es un zip con word/document.xml).</summary>
    private static string ExtractDocxText(byte[] bytes)
    {
        try
        {
            using var zip = new ZipArchive(new MemoryStream(bytes), ZipArchiveMode.Read);
            var entry = zip.GetEntry("word/document.xml");
            if (entry is null) return string.Empty;
            using var reader = new StreamReader(entry.Open());
            var xml = reader.ReadToEnd();
            xml = Regex.Replace(xml, @"</w:p>", "\n");
            xml = Regex.Replace(xml, @"<w:tab/>", "\t");
            var text = Regex.Replace(xml, @"<[^>]+>", string.Empty);
            return System.Net.WebUtility.HtmlDecode(text).Trim();
        }
        catch
        {
            return string.Empty;
        }
    }

    private static string? Str(JsonNode? n)
    {
        if (n is null) return null;
        var s = n is JsonValue v && v.TryGetValue<string>(out var str) ? str : n.ToString();
        return string.IsNullOrWhiteSpace(s) ? null : s.Trim();
    }

    private static int? ToInt(JsonNode? n)
    {
        if (n is null) return null;
        if (n is JsonValue v)
        {
            if (v.TryGetValue<int>(out var i)) return i;
            if (v.TryGetValue<double>(out var d)) return (int)Math.Round(d);
        }
        return int.TryParse(n.ToString(), out var parsed) ? parsed : null;
    }

    private static bool? ToBool(JsonNode? n)
    {
        if (n is null) return null;
        if (n is JsonValue v && v.TryGetValue<bool>(out var b)) return b;
        return bool.TryParse(n.ToString(), out var parsed) ? parsed : null;
    }
}
