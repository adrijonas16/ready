using System.Data;
using Npgsql;

namespace UtilesApi.Infrastructure.Database;

public interface IDbConnectionFactory
{
    IDbConnection CreateConnection();
}

public class DbConnectionFactory : IDbConnectionFactory
{
    private readonly string _connectionString;

    public DbConnectionFactory(IConfiguration configuration)
    {
        // Railway y otros servicios entregan la base como DATABASE_URL (postgresql://usuario:clave@host:puerto/base)
        var cadena = configuration.GetConnectionString("DefaultConnection");
        if (string.IsNullOrWhiteSpace(cadena)) cadena = configuration["DATABASE_URL"];
        if (string.IsNullOrWhiteSpace(cadena))
            throw new InvalidOperationException(
                "Falta ConnectionStrings:DefaultConnection. Defínela con la variable de entorno ConnectionStrings__DefaultConnection " +
                "(o DATABASE_URL) o, en tu equipo, en appsettings.Development.local.json (ignorado por git).");
        _connectionString = Normalizar(cadena);
    }

    public IDbConnection CreateConnection()
    {
        return new NpgsqlConnection(_connectionString);
    }

    /// <summary>
    /// Acepta la cadena de Npgsql ("Host=…;Database=…") o una URL "postgres://usuario:clave@host:puerto/base?sslmode=require".
    /// Npgsql no lee URLs, así que se convierten a su formato.
    /// </summary>
    public static string Normalizar(string cadena)
    {
        cadena = cadena.Trim();
        if (!cadena.StartsWith("postgres://", StringComparison.OrdinalIgnoreCase) &&
            !cadena.StartsWith("postgresql://", StringComparison.OrdinalIgnoreCase))
            return cadena;

        var uri = new Uri(cadena);
        var credenciales = uri.UserInfo.Split(':', 2);
        var b = new NpgsqlConnectionStringBuilder
        {
            Host = uri.Host,
            Port = uri.IsDefaultPort || uri.Port <= 0 ? 5432 : uri.Port,
            Database = Uri.UnescapeDataString(uri.AbsolutePath.TrimStart('/')),
            Username = Uri.UnescapeDataString(credenciales[0]),
            Password = credenciales.Length > 1 ? Uri.UnescapeDataString(credenciales[1]) : null,
        };
        foreach (var par in uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var kv = par.Split('=', 2);
            if (kv.Length == 2 && kv[0].Equals("sslmode", StringComparison.OrdinalIgnoreCase) &&
                Enum.TryParse<SslMode>(Uri.UnescapeDataString(kv[1]).Replace("-", ""), ignoreCase: true, out var modo))
                b.SslMode = modo;
        }
        return b.ConnectionString;
    }
}
