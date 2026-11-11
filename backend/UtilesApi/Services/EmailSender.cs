using System.Text.Json;
using System.Text.Json.Serialization;

namespace UtilesApi.Services;

/// <summary>Envío de correos. Si no está configurado, <see cref="Configurado"/> es false y no se envía nada.</summary>
public interface IEmailSender
{
    bool Configurado { get; }
    /// <summary>Remitente que verá el colegio (p. ej. "Ready &lt;listas@ready.pe&gt;"), o null si falta configurarlo.</summary>
    string? Remitente { get; }
    Task<ResultadoCorreo> Enviar(string para, string asunto, string texto, CancellationToken ct = default);
}

public record ResultadoCorreo(bool Ok, string? Error = null);

/// <summary>
/// Correo con Resend (https://resend.com). Variables: Email__ResendApiKey y Email__From
/// (un remitente de un dominio verificado en Resend, p. ej. "Ready &lt;listas@ready.pe&gt;"); Email__ReplyTo es opcional.
/// </summary>
public class ResendEmailSender : IEmailSender
{
    private readonly HttpClient _http;
    private readonly string? _apiKey;
    private readonly string? _from;
    private readonly string? _replyTo;
    private readonly ILogger<ResendEmailSender> _logger;

    public ResendEmailSender(HttpClient http, IConfiguration config, ILogger<ResendEmailSender> logger)
    {
        _http = http;
        _logger = logger;
        _apiKey = config["Email:ResendApiKey"];
        _from = config["Email:From"];
        _replyTo = config["Email:ReplyTo"];
        // Email__ResendBaseUrl solo para pruebas locales (un servidor falso); en producción, la API de Resend
        _http.BaseAddress ??= new Uri(config["Email:ResendBaseUrl"] ?? "https://api.resend.com/");
    }

    public bool Configurado => !string.IsNullOrWhiteSpace(_apiKey) && !string.IsNullOrWhiteSpace(_from);
    public string? Remitente => string.IsNullOrWhiteSpace(_from) ? null : _from;

    private sealed record Cuerpo(
        [property: JsonPropertyName("from")] string From,
        [property: JsonPropertyName("to")] string[] To,
        [property: JsonPropertyName("subject")] string Subject,
        [property: JsonPropertyName("text")] string Text,
        [property: JsonPropertyName("reply_to"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? ReplyTo);

    public async Task<ResultadoCorreo> Enviar(string para, string asunto, string texto, CancellationToken ct = default)
    {
        if (!Configurado) return new ResultadoCorreo(false, "El envío de correos no está configurado");
        using var req = new HttpRequestMessage(HttpMethod.Post, "emails")
        {
            // Cuerpo con largo conocido (sin "chunked"): lo aceptan todos los proxies
            Content = new StringContent(
                JsonSerializer.Serialize(new Cuerpo(_from!, new[] { para }, asunto, texto, string.IsNullOrWhiteSpace(_replyTo) ? null : _replyTo)),
                System.Text.Encoding.UTF8, "application/json")
        };
        req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", _apiKey);
        try
        {
            using var resp = await _http.SendAsync(req, ct);
            if (resp.IsSuccessStatusCode) return new ResultadoCorreo(true);
            var detalle = await resp.Content.ReadAsStringAsync(ct);
            _logger.LogWarning("Resend rechazó el correo ({Status}): {Detalle}", (int)resp.StatusCode, detalle.Length > 300 ? detalle[..300] : detalle);
            return new ResultadoCorreo(false, $"El servicio de correo respondió {(int)resp.StatusCode}");
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            _logger.LogWarning(ex, "No se pudo enviar el correo");
            return new ResultadoCorreo(false, "No se pudo conectar con el servicio de correo");
        }
    }
}
