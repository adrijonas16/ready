using System.Security.Cryptography;
using System.Text;

namespace UtilesApi.Infrastructure.Security;

/// <summary>
/// Revisa Jwt:Key al arrancar. Fuera de Development la API no arranca si la clave falta, es corta
/// o es una de las que estuvieron en el repositorio (cualquiera podría firmar un token de ADMIN).
/// En Development, si falta, se usa una clave aleatoria solo para ese proceso.
/// </summary>
public static class JwtKeyGuard
{
    public const int MinLength = 32;

    /// <summary>SHA-256 de claves que estuvieron publicadas en el repositorio (no se guardan en claro).</summary>
    private static readonly HashSet<string> ClavesPublicadas = new(StringComparer.OrdinalIgnoreCase)
    {
        // Jwt:Key de appsettings.json (commits d0969f8 … 3cc1393)
        "6c39d5b635a1aab68bc9ca0d6c0498bf9c8381abaa5b669b0cec18a084ddfaec",
        // Valor por defecto que usaban Program.cs y AuthService cuando faltaba la clave
        "e2fee058374c853c2682fa69829d3f29c4ce4653f58bff0a735493bc204212dd",
    };

    public static bool EsPublicada(string key) =>
        ClavesPublicadas.Contains(Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key))));

    /// <summary>Devuelve el motivo por el que la clave no sirve, o null si está bien.</summary>
    public static string? Problema(string? key)
    {
        if (string.IsNullOrWhiteSpace(key)) return "Jwt:Key no está configurada";
        if (Encoding.UTF8.GetByteCount(key) < MinLength) return $"Jwt:Key debe tener al menos {MinLength} caracteres";
        if (EsPublicada(key)) return "Jwt:Key es una clave que estuvo publicada en el repositorio";
        return null;
    }

    /// <summary>
    /// Deja en configuración una clave válida o lanza una excepción (fuera de Development).
    /// Devuelve la clave a usar.
    /// </summary>
    public static string Asegurar(ConfigurationManager config, IHostEnvironment env)
    {
        var key = config["Jwt:Key"];
        var problema = Problema(key);
        if (problema == null) return key!;

        if (!env.IsDevelopment())
            throw new InvalidOperationException(
                $"{problema}. Define la variable de entorno Jwt__Key con un secreto propio de 32 caracteres o más " +
                "(por ejemplo: openssl rand -base64 48). La API no arranca sin ella.");

        // Development: clave aleatoria para este proceso (los tokens dejan de valer al reiniciar)
        var aleatoria = Convert.ToBase64String(RandomNumberGenerator.GetBytes(48));
        config["Jwt:Key"] = aleatoria;
        Console.WriteLine($"[Development] {problema}: se usa una clave aleatoria temporal. Define Jwt__Key para que las sesiones sobrevivan a un reinicio.");
        return aleatoria;
    }
}
