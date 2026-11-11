using System.Globalization;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.RateLimiting;
using UtilesApi.DTOs;

namespace UtilesApi.Infrastructure.Security;

/// <summary>
/// Límites por IP del cliente (la IP sale de X-Forwarded-For / CF-Connecting-IP, ver ProxyHeaders).
/// Se guardan en memoria del proceso: con una sola instancia en Render es suficiente.
/// </summary>
public static class RateLimits
{
    public const string Login = "login";
    public const string Register = "register";
    public const string Upload = "upload";
    public const string Observaciones = "observaciones";
    public const string Items = "items";
    public const string Orders = "orders";
    public const string Consultas = "consultas";

    public const string MensajeLimite = "Demasiados intentos. Espera un momento.";

    public static string ClientIp(HttpContext ctx) => ctx.Connection.RemoteIpAddress?.ToString() ?? "sin-ip";

    private static RateLimitPartition<string> PorIp(HttpContext ctx, string politica, int permisos, TimeSpan ventana) =>
        RateLimitPartition.GetFixedWindowLimiter($"{politica}:{ClientIp(ctx)}", _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = permisos,
            Window = ventana,
            QueueLimit = 0,
            AutoReplenishment = true
        });

    public static IServiceCollection AddReadyRateLimits(this IServiceCollection services, IConfiguration config)
    {
        var habilitado = config.GetValue("RateLimit:Enabled", true);
        var globalPorMinuto = config.GetValue("RateLimit:GlobalPerMinute", 300);

        services.AddRateLimiter(options =>
        {
            options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;

            if (habilitado)
            {
                // Global: 300 peticiones por minuto por IP (no cuenta /health, que Render consulta seguido)
                options.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(ctx =>
                    ctx.Request.Path.StartsWithSegments("/health")
                        ? RateLimitPartition.GetNoLimiter("health")
                        : PorIp(ctx, "global", globalPorMinuto, TimeSpan.FromMinutes(1)));
            }

            options.AddPolicy(Login, ctx => habilitado ? PorIp(ctx, Login, 10, TimeSpan.FromMinutes(1)) : RateLimitPartition.GetNoLimiter(Login));
            options.AddPolicy(Register, ctx => habilitado ? PorIp(ctx, Register, 10, TimeSpan.FromMinutes(1)) : RateLimitPartition.GetNoLimiter(Register));
            options.AddPolicy(Upload, ctx => habilitado ? PorIp(ctx, Upload, 5, TimeSpan.FromMinutes(10)) : RateLimitPartition.GetNoLimiter(Upload));
            options.AddPolicy(Observaciones, ctx => habilitado ? PorIp(ctx, Observaciones, 30, TimeSpan.FromMinutes(10)) : RateLimitPartition.GetNoLimiter(Observaciones));
            options.AddPolicy(Items, ctx => habilitado ? PorIp(ctx, Items, 30, TimeSpan.FromMinutes(10)) : RateLimitPartition.GetNoLimiter(Items));
            options.AddPolicy(Orders, ctx => habilitado ? PorIp(ctx, Orders, 10, TimeSpan.FromMinutes(10)) : RateLimitPartition.GetNoLimiter(Orders));
            options.AddPolicy(Consultas, ctx => habilitado ? PorIp(ctx, Consultas, 10, TimeSpan.FromHours(1)) : RateLimitPartition.GetNoLimiter(Consultas));

            options.OnRejected = async (context, token) =>
            {
                var segundos = context.Lease.TryGetMetadata(MetadataName.RetryAfter, out var retry)
                    ? (int)Math.Ceiling(retry.TotalSeconds)
                    : 60;
                await EscribirLimite(context.HttpContext, Math.Max(1, segundos), MensajeLimite, token);

                var logger = context.HttpContext.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger("RateLimit");
                logger.LogWarning("Límite de peticiones: {Ip} {Method} {Path}",
                    ClientIp(context.HttpContext), context.HttpContext.Request.Method, context.HttpContext.Request.Path);
            };
        });
        return services;
    }

    /// <summary>Respuesta 429 con Retry-After y el formato { success:false, error:{ code:'RATE_LIMIT', message } }.</summary>
    public static Task EscribirLimite(HttpContext ctx, int segundos, string mensaje, CancellationToken token = default)
    {
        ctx.Response.StatusCode = StatusCodes.Status429TooManyRequests;
        ctx.Response.Headers.RetryAfter = segundos.ToString(CultureInfo.InvariantCulture);
        return ctx.Response.WriteAsJsonAsync(ApiResponse.Fail("RATE_LIMIT", mensaje), token);
    }
}
