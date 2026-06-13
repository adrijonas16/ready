using System.Text;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.Tokens;
using UtilesApi.Infrastructure.Database;
using UtilesApi.Infrastructure.OCR;
using UtilesApi.Infrastructure.Storage;
using UtilesApi.Services;
using UtilesApi.Infrastructure.Database;

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
        IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwtKey))
    };
});

builder.Services.AddCors(options =>
{
    options.AddPolicy("AllowAll", policy =>
    {
        policy.AllowAnyOrigin()
              .AllowAnyMethod()
              .AllowAnyHeader();
    });
});

var app = builder.Build();

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