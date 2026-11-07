using System.Globalization;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using UtilesApi.DTOs;

namespace UtilesApi.Infrastructure.Security;

/// <summary>
/// Anti-spam para formularios públicos. El frontend manda:
/// - <c>sitioWeb</c>: campo oculto que una persona deja vacío (los bots lo llenan);
/// - <c>_t</c>: milisegundos que el formulario estuvo abierto (menos de 2,5 s es un bot).
/// Llegan en el JSON del cuerpo o como campos del formulario (multipart). Si no vienen se acepta
/// (clientes antiguos). Corre antes del model binding y deja el cuerpo listo para volver a leerse.
/// </summary>
[AttributeUsage(AttributeTargets.Method | AttributeTargets.Class)]
public sealed class HoneypotAttribute : Attribute, IAsyncResourceFilter
{
    public const string CampoTrampa = "sitioWeb";
    public const string CampoTiempo = "_t";
    public const double MinimoMs = 2500;
    private const long MaxJsonBytes = 1024 * 1024;

    public async Task OnResourceExecutionAsync(ResourceExecutingContext context, ResourceExecutionDelegate next)
    {
        var req = context.HttpContext.Request;
        string? motivo = null;

        if (req.HasFormContentType)
        {
            var form = await req.ReadFormAsync(context.HttpContext.RequestAborted);
            motivo = Revisar(
                form.TryGetValue(CampoTrampa, out var trampa) ? trampa.ToString() : null,
                form.TryGetValue(CampoTiempo, out var t) ? t.ToString() : null,
                tiempoPresente: form.ContainsKey(CampoTiempo));
        }
        else if (req.HasJsonContentType() && (req.ContentLength ?? 0) <= MaxJsonBytes)
        {
            req.EnableBuffering();
            try
            {
                using var doc = await JsonDocument.ParseAsync(req.Body, cancellationToken: context.HttpContext.RequestAborted);
                if (doc.RootElement.ValueKind == JsonValueKind.Object)
                {
                    string? trampa = null, tiempo = null;
                    bool hayTrampa = false, hayTiempo = false;
                    foreach (var prop in doc.RootElement.EnumerateObject())
                    {
                        if (string.Equals(prop.Name, CampoTrampa, StringComparison.OrdinalIgnoreCase))
                        {
                            hayTrampa = true;
                            trampa = prop.Value.ValueKind switch
                            {
                                JsonValueKind.Null or JsonValueKind.Undefined => null,
                                JsonValueKind.String => prop.Value.GetString(),
                                _ => prop.Value.GetRawText()
                            };
                        }
                        else if (prop.Name == CampoTiempo)
                        {
                            hayTiempo = prop.Value.ValueKind != JsonValueKind.Null;
                            tiempo = prop.Value.ValueKind switch
                            {
                                JsonValueKind.String => prop.Value.GetString(),
                                JsonValueKind.Number => prop.Value.GetRawText(),
                                JsonValueKind.Null => null,
                                _ => "x"
                            };
                        }
                    }
                    motivo = Revisar(hayTrampa ? trampa : null, tiempo, hayTiempo);
                }
            }
            catch (JsonException)
            {
                // JSON inválido: lo rechaza el model binding con su propio mensaje
            }
            finally
            {
                req.Body.Position = 0;
            }
        }

        if (motivo != null)
        {
            var logger = context.HttpContext.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger("AntiSpam");
            logger.LogWarning("Envío rechazado por anti-spam ({Motivo}): {Ip} {Path}",
                motivo, RateLimits.ClientIp(context.HttpContext), req.Path);
            context.Result = new BadRequestObjectResult(ApiResponse.Fail("SPAM_DETECTED",
                "No pudimos procesar el envío. Espera unos segundos y vuelve a intentarlo."));
            return;
        }

        await next();
    }

    /// <summary>Devuelve el motivo del rechazo o null si pasa.</summary>
    public static string? Revisar(string? trampa, string? tiempo, bool tiempoPresente)
    {
        if (!string.IsNullOrWhiteSpace(trampa)) return "campo trampa lleno";
        if (!tiempoPresente || string.IsNullOrWhiteSpace(tiempo)) return null;
        if (!double.TryParse(tiempo.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var ms) || double.IsNaN(ms))
            return "tiempo inválido";
        if (ms < MinimoMs) return "enviado demasiado rápido";
        return null;
    }
}
