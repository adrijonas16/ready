using System.Security.Claims;
using System.Text;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.FileProviders;
using Microsoft.IdentityModel.Tokens;
using UtilesApi.Infrastructure.Database;
using UtilesApi.Infrastructure.OCR;
using UtilesApi.Infrastructure.Storage;
using UtilesApi.Services;
using UtilesApi.Infrastructure.AI;
using UtilesApi.DTOs;
using UtilesApi.Infrastructure.Security;

Dapper.DefaultTypeMap.MatchNamesWithUnderscores = true;

var builder = WebApplication.CreateBuilder(args);

// Secretos locales (cadena de conexión, Jwt:Key, AI:ApiKey…) fuera de git: appsettings.Development.local.json.
// En Render/producción todo llega por variables de entorno (ConnectionStrings__DefaultConnection, Jwt__Key, …).
builder.Configuration.AddJsonFile($"appsettings.{builder.Environment.EnvironmentName}.local.json", optional: true, reloadOnChange: false);

// Sin cabecera "Server" y con cuerpo pequeño por defecto (JSON). Las subidas de archivos
// suben su propio límite con [RequestSizeLimit] (25 MB).
builder.WebHost.ConfigureKestrel(o =>
{
    o.AddServerHeader = false;
    o.Limits.MaxRequestBodySize = builder.Configuration.GetValue<long>("Limits:MaxRequestBodyBytes", 2 * 1024 * 1024);
});
builder.Services.Configure<Microsoft.AspNetCore.Http.Features.FormOptions>(o =>
{
    o.MultipartBodyLengthLimit = Archivos.MaxBytesSubida;
    o.ValueLengthLimit = 64 * 1024;
    o.ValueCountLimit = 200;
});

builder.Services.AddControllers()
    .ConfigureApiBehaviorOptions(o =>
    {
        // Errores de formato del cuerpo con el mismo { success, error } en español que el resto de la API
        o.InvalidModelStateResponseFactory = ctx =>
        {
            var primero = ctx.ModelState.Where(e => e.Value?.Errors.Count > 0).Select(e => e.Key).FirstOrDefault();
            var mensaje = string.IsNullOrEmpty(primero)
                ? "Los datos enviados no son válidos"
                : $"Los datos enviados no son válidos (revisa \"{primero.TrimStart('$', '.')}\")";
            return new BadRequestObjectResult(ApiResponse.Fail("INVALID_REQUEST", mensaje));
        };
    });
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

builder.Services.AddSingleton<IDbConnectionFactory, DbConnectionFactory>();
builder.Services.AddSingleton<DatabaseInitializer>();

builder.Services.AddScoped<UserRepository>();
builder.Services.AddScoped<SchoolRepository>();
builder.Services.AddScoped<GradeRepository>();
builder.Services.AddScoped<SupplyListRepository>();
builder.Services.AddScoped<SupplyItemRepository>();
builder.Services.AddScoped<ProductRepository>();
builder.Services.AddScoped<OrderRepository>();
builder.Services.AddScoped<OrderItemRepository>();
builder.Services.AddScoped<AdditionalCostRepository>();
builder.Services.AddScoped<SectionRepository>();
builder.Services.AddScoped<CategoryRepository>();

var storageProvider = builder.Configuration["Storage:Provider"] ?? "local";
if (storageProvider == "r2")
    builder.Services.AddSingleton<IStorageService, R2StorageService>();
else
    builder.Services.AddSingleton<IStorageService, LocalStorageService>();
builder.Services.AddSingleton<IOcrService, MockOcrService>();
builder.Services.AddSingleton<ListParserService>();

builder.Services.AddSignalR();
builder.Services.AddSingleton<INotificationService, NotificationService>();

builder.Services.AddScoped<IAuthService, AuthService>();
builder.Services.AddScoped<IProductMatchingService, ProductMatchingService>();
builder.Services.AddScoped<ListProcessingService>();

// IA (Claude) para leer listas de útiles. Clave en AI:ApiKey, AI__ApiKey o ANTHROPIC_API_KEY.
var aiOptions = builder.Configuration.GetSection("AI").Get<AiOptions>() ?? new AiOptions();
if (string.IsNullOrWhiteSpace(aiOptions.ApiKey))
    aiOptions.ApiKey = Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY") ?? string.Empty;
builder.Services.AddSingleton(aiOptions);
builder.Services.AddHttpClient<ClaudeListReader>(c => c.Timeout = TimeSpan.FromMinutes(8));

// Fuera de Development no arranca sin una Jwt:Key propia (falta, es corta o es la que estuvo en el repositorio)
var jwtKey = JwtKeyGuard.Asegurar(builder.Configuration, builder.Environment);
var jwtIssuer = builder.Configuration["Jwt:Issuer"] ?? "UtilesApi";
var jwtAudience = builder.Configuration["Jwt:Audience"] ?? "UtilesClient";

builder.Services.AddAuthentication(options =>
{
    options.DefaultAuthenticateScheme = JwtBearerDefaults.AuthenticationScheme;
    options.DefaultChallengeScheme = JwtBearerDefaults.AuthenticationScheme;
})
.AddJwtBearer(options =>
{
    options.TokenValidationParameters = new TokenValidationParameters
    {
        ValidateIssuer = true,
        ValidateAudience = true,
        ValidateLifetime = true,
        ValidateIssuerSigningKey = true,
        ValidIssuer = jwtIssuer,
        ValidAudience = jwtAudience,
        IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwtKey)),
        // AuthService.GenerateToken escribe ClaimTypes.Role / NameIdentifier (en el token quedan como "role" / "nameid")
        // y el manejador los vuelve a mapear al leerlos. [Authorize(Roles = ...)] lee este tipo de claim.
        RoleClaimType = ClaimTypes.Role,
        NameClaimType = ClaimTypes.NameIdentifier
    };
    options.MapInboundClaims = true;
    options.Events = new JwtBearerEvents
    {
        // SignalR no puede mandar cabeceras desde el navegador: el token llega como ?access_token=
        OnMessageReceived = ctx =>
        {
            var accessToken = ctx.Request.Query["access_token"];
            if (!string.IsNullOrEmpty(accessToken) && ctx.HttpContext.Request.Path.StartsWithSegments("/hub"))
                ctx.Token = accessToken;
            return Task.CompletedTask;
        },
        // Respuestas 401/403 con el mismo formato { success, error } que el resto de la API
        OnChallenge = async ctx =>
        {
            ctx.HandleResponse();
            ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
            await ctx.Response.WriteAsJsonAsync(ApiResponse.Fail("UNAUTHORIZED", "Inicia sesión para continuar"));
        },
        OnForbidden = async ctx =>
        {
            ctx.Response.StatusCode = StatusCodes.Status403Forbidden;
            await ctx.Response.WriteAsJsonAsync(ApiResponse.Fail("FORBIDDEN", "No tienes permiso para esta acción"));
        }
    };
});
builder.Services.AddAuthorization();

// HTTPS detrás del proxy de Render, HSTS, CORS con lista de orígenes (Cors__Origins) y límites de peticiones
builder.Services.AddReadyHttpSecurity(builder.Configuration);
builder.Services.AddReadyRateLimits(builder.Configuration);
builder.Services.AddSingleton(_ => new LoginGuard());

var app = builder.Build();

using (var scope = app.Services.CreateScope())
{
    var dbInit = scope.ServiceProvider.GetRequiredService<DatabaseInitializer>();
    dbInit.Initialize();
}

// 1. IP y esquema reales detrás del proxy (X-Forwarded-For / X-Forwarded-Proto; CF-Connecting-IP en Render)
app.UseForwardedHeaders();
app.UseClientIpFromCloudflare(builder.Configuration);

// 2. Errores: en producción nunca se devuelve el detalle (stack trace), solo un JSON genérico
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler(errores => errores.Run(async ctx =>
    {
        var ex = ctx.Features.Get<IExceptionHandlerFeature>()?.Error;
        if (ex is BadHttpRequestException bad)
        {
            ctx.Response.StatusCode = bad.StatusCode;
            var mensaje = bad.StatusCode == StatusCodes.Status413PayloadTooLarge
                ? "El envío es demasiado grande"
                : "La petición no es válida";
            await ctx.Response.WriteAsJsonAsync(ApiResponse.Fail(bad.StatusCode == 413 ? "PAYLOAD_TOO_LARGE" : "BAD_REQUEST", mensaje));
            return;
        }
        ctx.Response.StatusCode = StatusCodes.Status500InternalServerError;
        await ctx.Response.WriteAsJsonAsync(ApiResponse.Fail("SERVER_ERROR", "Ocurrió un error inesperado. Inténtalo de nuevo en unos minutos."));
    }));

    // 3. HTTPS obligatorio (Render ya termina TLS; X-Forwarded-Proto dice si el cliente usó https).
    //    /health queda fuera para que el chequeo interno de Render (http) reciba 200.
    app.UseHsts();
    app.UseWhen(ctx => !ctx.Request.Path.StartsWithSegments("/health"), rama => rama.UseHttpsRedirection());
}

// 4. Cabeceras de seguridad en todas las respuestas
app.UseSecurityHeaders();

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

// Archivos subidos con Storage:Provider=local (en producción se usa R2 y se sirven desde su dominio)
if (!string.Equals(storageProvider, "r2", StringComparison.OrdinalIgnoreCase))
{
    var carpeta = Path.GetFullPath(builder.Configuration["Storage:LocalPath"] ?? Path.Combine(Directory.GetCurrentDirectory(), "uploads"));
    Directory.CreateDirectory(carpeta);
    app.UseStaticFiles(new StaticFileOptions
    {
        FileProvider = new PhysicalFileProvider(carpeta),
        RequestPath = "/uploads",
        ServeUnknownFileTypes = false
    });
}

app.UseRouting();
app.UseCors(HttpSecurity.PoliticaCors);
app.UseRateLimiter();

app.UseAuthentication();
app.UseAuthorization();

// Chequeo de salud para Render (Health Check Path = /health)
app.MapGet("/health", () => Results.Ok(new { status = "ok" }))
    .AllowAnonymous()
    .DisableRateLimiting()
    .ExcludeFromDescription();

app.MapControllers();
app.MapHub<NotificationHub>("/hub/notifications");

app.Run();

public partial class Program { }
