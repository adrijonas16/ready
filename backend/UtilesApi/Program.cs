using System.Security.Claims;
using System.Text;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.Tokens;
using UtilesApi.Infrastructure.Database;
using UtilesApi.Infrastructure.OCR;
using UtilesApi.Infrastructure.Storage;
using UtilesApi.Services;
using UtilesApi.Infrastructure.AI;
using UtilesApi.DTOs;

Dapper.DefaultTypeMap.MatchNamesWithUnderscores = true;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddControllers();
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

var jwtKey = builder.Configuration["Jwt:Key"] ?? "DefaultSecretKey123456789012345678901234";
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

builder.Services.AddCors(options =>
{
    options.AddPolicy("AllowAll", policy =>
    {
        policy.AllowAnyOrigin()
              .AllowAnyMethod()
              .AllowAnyHeader()
              .WithExposedHeaders("X-Total-Count");
    });
});

var app = builder.Build();

// Con la clave por defecto (o la del repositorio) cualquiera puede firmar un token de ADMIN.
if (!app.Environment.IsDevelopment() &&
    (builder.Configuration["Jwt:Key"] is null || jwtKey == "UtilesPlatformSecretKey2024SuperSecure12345678901234567890"))
{
    app.Logger.LogCritical("Jwt:Key no está configurada o es la clave del repositorio. Define Jwt__Key con un valor secreto propio.");
}

using (var scope = app.Services.CreateScope())
{
    var dbInit = scope.ServiceProvider.GetRequiredService<DatabaseInitializer>();
    dbInit.Initialize();
}

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseCors("AllowAll");

app.UseAuthentication();
app.UseAuthorization();

app.MapControllers();
app.MapHub<NotificationHub>("/hub/notifications");

app.UseStaticFiles();

app.Run();

public partial class Program { }