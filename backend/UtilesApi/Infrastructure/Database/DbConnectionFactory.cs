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
        var cadena = configuration.GetConnectionString("DefaultConnection");
        if (string.IsNullOrWhiteSpace(cadena))
            throw new InvalidOperationException(
                "Falta ConnectionStrings:DefaultConnection. Defínela con la variable de entorno ConnectionStrings__DefaultConnection " +
                "o, en tu equipo, en appsettings.Development.local.json (ignorado por git).");
        _connectionString = cadena;
    }

    public IDbConnection CreateConnection()
    {
        return new NpgsqlConnection(_connectionString);
    }
}