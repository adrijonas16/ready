using Dapper;

namespace UtilesApi.Infrastructure.Database;

/// <summary>Consulta de un producto que el cliente no encontró en la tienda.</summary>
public class Consulta
{
    public Guid Id { get; set; }
    public Guid? UserId { get; set; }
    public Guid? SupplyListId { get; set; }
    public string Producto { get; set; } = string.Empty;
    public string? Detalle { get; set; }
    public string? Nombre { get; set; }
    public string? Contacto { get; set; }
    public string Estado { get; set; } = EstadosConsulta.Pendiente;
    public string? Respuesta { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? RespondidaAt { get; set; }
    // Datos del cliente con cuenta (solo para el panel)
    public string? UserName { get; set; }
    public string? UserEmail { get; set; }
    public string? UserPhone { get; set; }
}

public static class EstadosConsulta
{
    public const string Pendiente = "PENDIENTE";
    public const string Disponible = "DISPONIBLE";
    public const string NoDisponible = "NO_DISPONIBLE";
    public static readonly string[] Todos = { Pendiente, Disponible, NoDisponible };
}

public class ConsultaRepository
{
    private readonly IDbConnectionFactory _db;
    public ConsultaRepository(IDbConnectionFactory db) { _db = db; }

    private const string Select = @"
        SELECT c.id AS Id, c.user_id AS UserId, c.supply_list_id AS SupplyListId, c.producto AS Producto, c.detalle AS Detalle,
               c.nombre AS Nombre, c.contacto AS Contacto, c.estado AS Estado, c.respuesta AS Respuesta,
               c.created_at AS CreatedAt, c.respondida_at AS RespondidaAt,
               u.name AS UserName, u.email AS UserEmail, u.phone AS UserPhone
        FROM product_inquiries c
        LEFT JOIN users u ON u.id = c.user_id";

    public async Task<Guid> Create(Consulta c)
    {
        using var conn = _db.CreateConnection();
        return await conn.ExecuteScalarAsync<Guid>(@"
            INSERT INTO product_inquiries (user_id, supply_list_id, producto, detalle, nombre, contacto)
            VALUES (@UserId, (SELECT id FROM supply_lists WHERE id = @SupplyListId), @Producto, @Detalle, @Nombre, @Contacto)
            RETURNING id", c);
    }

    public async Task<Consulta?> GetById(Guid id)
    {
        using var conn = _db.CreateConnection();
        return await conn.QueryFirstOrDefaultAsync<Consulta>($"{Select} WHERE c.id = @Id", new { Id = id });
    }

    public async Task<IEnumerable<Consulta>> GetByUser(Guid userId)
    {
        using var conn = _db.CreateConnection();
        return await conn.QueryAsync<Consulta>($"{Select} WHERE c.user_id = @UserId ORDER BY c.created_at DESC LIMIT 200", new { UserId = userId });
    }

    /// <summary>Panel: pendientes primero (la más antigua arriba, es la que vence antes); luego las respondidas, recientes primero.</summary>
    public async Task<IEnumerable<Consulta>> GetAll(string? estado)
    {
        using var conn = _db.CreateConnection();
        return await conn.QueryAsync<Consulta>($@"{Select}
            WHERE (@Estado::text IS NULL OR c.estado = @Estado)
            ORDER BY (c.estado = 'PENDIENTE') DESC,
                     CASE WHEN c.estado = 'PENDIENTE' THEN c.created_at END ASC,
                     c.respondida_at DESC NULLS LAST
            LIMIT 500", new { Estado = estado });
    }

    public async Task<int> CountPendientesDelUsuario(Guid? userId, string? contacto)
    {
        using var conn = _db.CreateConnection();
        return await conn.ExecuteScalarAsync<int>(@"
            SELECT COUNT(*) FROM product_inquiries
            WHERE estado = 'PENDIENTE' AND ((@UserId::uuid IS NOT NULL AND user_id = @UserId) OR (@UserId::uuid IS NULL AND contacto = @Contacto))",
            new { UserId = userId, Contacto = contacto });
    }

    public async Task<bool> Responder(Guid id, string estado, string? respuesta, Guid? adminId)
    {
        using var conn = _db.CreateConnection();
        return await conn.ExecuteAsync(@"
            UPDATE product_inquiries SET estado = @Estado, respuesta = @Respuesta, respondida_por = @AdminId, respondida_at = NOW()
            WHERE id = @Id", new { Id = id, Estado = estado, Respuesta = respuesta, AdminId = adminId }) > 0;
    }
}
