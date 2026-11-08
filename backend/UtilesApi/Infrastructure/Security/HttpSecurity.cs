using System.Net;
using Microsoft.AspNetCore.HttpOverrides;

namespace UtilesApi.Infrastructure.Security;

/// <summary>Proxy de Render (HTTPS, IP real del cliente), cabeceras de seguridad y CORS.</summary>
public static class HttpSecurity
{
    /// <summary>Orígenes del frontend permitidos si no se configura Cors:Origins / Cors__Origins.</summary>
    public static readonly string[] OrigenesPorDefecto =
    {
        "https://ready-web-gamma.vercel.app",
        "https://ready-web-rf43.vercel.app",
        "http://localhost:3000",
        "http://localhost:3005",
    };

    public const string PoliticaCors = "Frontend";

    /// <summary>
    /// Lee Cors:Origins como texto separado por comas (Cors__Origins=https://a,https://b)
    /// o como arreglo en appsettings (Cors:Origins:0, Cors:Origins:1 …).
    /// </summary>
    public static string[] OrigenesCors(IConfiguration config)
    {
        var lista = new List<string>();
        var texto = config["Cors:Origins"];
        if (!string.IsNullOrWhiteSpace(texto)) lista.AddRange(texto.Split(',', ';', ' ', '\n'));
        lista.AddRange(config.GetSection("Cors:Origins").GetChildren().Select(c => c.Value ?? ""));
        var origenes = lista.Select(o => o.Trim().TrimEnd('/')).Where(o => o.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        return origenes.Length > 0 ? origenes : OrigenesPorDefecto;
    }

    public static IServiceCollection AddReadyHttpSecurity(this IServiceCollection services, IConfiguration config)
    {
        services.Configure<ForwardedHeadersOptions>(o =>
        {
            o.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
            // Render no tiene IPs de proxy fijas: se confía en la cadena que llega al contenedor (no es accesible
            // directamente desde internet). En Render la cadena es "cliente, Cloudflare" → se recorren 2 saltos.
            o.KnownNetworks.Clear();
            o.KnownProxies.Clear();
            o.ForwardLimit = config.GetValue("ForwardedHeaders:ForwardLimit", 2);
        });

        services.AddHsts(o =>
        {
            o.MaxAge = TimeSpan.FromDays(730);
            o.IncludeSubDomains = true;
            o.Preload = true;
        });
        services.AddHttpsRedirection(o =>
        {
            o.RedirectStatusCode = StatusCodes.Status308PermanentRedirect;
            o.HttpsPort = 443;
        });

        var origenes = OrigenesCors(config);
        services.AddCors(options =>
        {
            options.AddPolicy(PoliticaCors, policy =>
            {
                // Sin AllowCredentials: la API usa "Authorization: Bearer" (SignalR con accessTokenFactory), no cookies
                policy.WithOrigins(origenes)
                      .AllowAnyMethod()
                      .AllowAnyHeader()
                      .WithExposedHeaders("X-Total-Count", "Retry-After")
                      .SetPreflightMaxAge(TimeSpan.FromHours(1));
            });
        });
        return services;
    }

    /// <summary>
    /// Detrás de Cloudflare (Render) la IP real del visitante viene en CF-Connecting-IP, que Cloudflare
    /// siempre reescribe. Se usa si existe y ForwardedHeaders:UseCfConnectingIp no es false.
    /// </summary>
    public static IApplicationBuilder UseClientIpFromCloudflare(this IApplicationBuilder app, IConfiguration config)
    {
        if (!config.GetValue("ForwardedHeaders:UseCfConnectingIp", true)) return app;
        return app.Use((ctx, next) =>
        {
            var cf = ctx.Request.Headers["CF-Connecting-IP"].ToString();
            if (!string.IsNullOrWhiteSpace(cf) && IPAddress.TryParse(cf.Trim(), out var ip))
                ctx.Connection.RemoteIpAddress = ip;
            return next(ctx);
        });
    }

    /// <summary>
    /// Cabeceras de seguridad. La API solo responde JSON: CSP "default-src 'none'" no rompe nada.
    /// Los archivos subidos se sirven desde R2 (otro dominio); con Storage:Provider=local se sirven en /uploads
    /// con CSP + sandbox, que tampoco impide mostrarlos en un &lt;img&gt; del frontend.
    /// Swagger (solo en Development) queda sin CSP para que cargue su interfaz.
    /// </summary>
    public static IApplicationBuilder UseSecurityHeaders(this IApplicationBuilder app)
    {
        return app.Use((ctx, next) =>
        {
            ctx.Response.OnStarting(() =>
            {
                var h = ctx.Response.Headers;
                h["X-Content-Type-Options"] = "nosniff";
                h["X-Frame-Options"] = "DENY";
                h["Referrer-Policy"] = "no-referrer";
                h["Permissions-Policy"] = "camera=(), microphone=(), geolocation=(), payment=()";
                h["Cross-Origin-Opener-Policy"] = "same-origin";
                h.Remove("X-Powered-By");

                var path = ctx.Request.Path;
                if (path.StartsWithSegments("/swagger")) return Task.CompletedTask;
                h["Content-Security-Policy"] = path.StartsWithSegments("/uploads")
                    ? "default-src 'none'; img-src 'self'; style-src 'unsafe-inline'; frame-ancestors 'none'; sandbox"
                    : "default-src 'none'; frame-ancestors 'none'";
                if (!path.StartsWithSegments("/uploads") && !h.ContainsKey("Cache-Control"))
                    h["Cache-Control"] = "no-store";
                return Task.CompletedTask;
            });
            return next(ctx);
        });
    }
}
