using System.Security.Cryptography;
using System.Text.Json;
using Dapper;
using UtilesApi.Infrastructure.Database;

namespace UtilesApi.Services;

/// <summary>Estados del seguimiento de cada colegio al pedirle su lista de útiles.</summary>
public static class EstadosContacto
{
    public const string SinContactar = "SIN_CONTACTAR";
    public const string Contactado = "CONTACTADO";
    public const string Respondio = "RESPONDIO";
    public const string ListaRecibida = "LISTA_RECIBIDA";
    public const string NoInteresado = "NO_INTERESADO";
    public static readonly string[] Todos = { SinContactar, Contactado, Respondio, ListaRecibida, NoInteresado };
}

public static class CanalesContacto
{
    public const string WhatsApp = "WHATSAPP";
    public const string Correo = "CORREO";
    public const string Llamada = "LLAMADA";
    public const string Enlace = "ENLACE"; // el colegio subió su lista por su enlace
    public const string Nota = "NOTA";
    public static readonly string[] Manuales = { WhatsApp, Correo, Llamada, Nota };
}

/// <summary>Mensaje y reglas para pedir las listas. Se guarda en contacto_colegios_config.</summary>
public class ConfigContacto
{
    public const string MensajePorDefecto =
        "Buenos días, le saluda Ready, tienda de útiles escolares en Arequipa.\n\n" +
        "Queremos ayudar a las familias de {colegio} a conseguir su lista de útiles {anio} completa y a buen precio. " +
        "¿Nos podrían compartir la lista de útiles de cada grado?\n\n" +
        "Pueden subirla aquí, sin crear cuenta: {enlace}\n" +
        "o respondernos por este medio con una foto o el PDF.\n\n" +
        "¡Muchas gracias!";

    public const string RecordatorioPorDefecto =
        "Buenos días, le escribimos nuevamente de Ready. ¿Ya tienen la lista de útiles {anio} de {colegio}? " +
        "Pueden subirla aquí: {enlace}\n\nSi prefieren que no les escribamos más, respondan BAJA. ¡Gracias!";

    public string Mensaje { get; set; } = MensajePorDefecto;
    public string Recordatorio { get; set; } = RecordatorioPorDefecto;
    public string Asunto { get; set; } = "Lista de útiles {anio} de {colegio}";
    /// <summary>Días entre un contacto y el siguiente recordatorio.</summary>
    public int DiasRecordatorio { get; set; } = 4;
    /// <summary>Cuántos recordatorios automáticos por correo como máximo.</summary>
    public int MaxRecordatorios { get; set; } = 2;
    /// <summary>Si está activo, los recordatorios por correo se envían solos.</summary>
    public bool RecordatoriosAutomaticos { get; set; } = true;

    public const int MaxTexto = 3000;

    public string? Validar()
    {
        if (string.IsNullOrWhiteSpace(Mensaje) || Mensaje.Length > MaxTexto) return $"Escribe el mensaje (hasta {MaxTexto} caracteres)";
        if (string.IsNullOrWhiteSpace(Recordatorio) || Recordatorio.Length > MaxTexto) return $"Escribe el recordatorio (hasta {MaxTexto} caracteres)";
        if (string.IsNullOrWhiteSpace(Asunto) || Asunto.Length > 200) return "Escribe el asunto del correo (hasta 200 caracteres)";
        if (!Mensaje.Contains("{enlace}")) return "El mensaje debe incluir {enlace} para que el colegio pueda subir su lista";
        if (DiasRecordatorio is < 1 or > 60) return "Los días entre recordatorios deben estar entre 1 y 60";
        if (MaxRecordatorios is < 0 or > 10) return "Los recordatorios deben estar entre 0 y 10";
        if (!Validacion.SinControl(Mensaje) || !Validacion.SinControl(Recordatorio) || !Validacion.SinControl(Asunto)) return "El texto tiene caracteres no permitidos";
        return null;
    }
}

/// <summary>Datos de contacto y seguimiento de un colegio.</summary>
public class ColegioContacto
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? Address { get; set; }
    public string? Gestion { get; set; }
    public string? Telefono { get; set; }
    public string? Whatsapp { get; set; }
    public string? Correo { get; set; }
    public string? ContactoNombre { get; set; }
    public string? ContactoNotas { get; set; }
    public string ContactoEstado { get; set; } = EstadosContacto.SinContactar;
    public DateTime? UltimoContactoAt { get; set; }
    public DateTime? ProximoContactoAt { get; set; }
    public int Recordatorios { get; set; }
    public string? EnlaceToken { get; set; }
    /// <summary>Listas aprobadas del año escolar en curso.</summary>
    public int ListasAprobadas { get; set; }
    /// <summary>Listas que el colegio subió y esperan revisión.</summary>
    public int ListasPorRevisar { get; set; }
    public string? Niveles { get; set; }
}

public class RegistroContacto
{
    public Guid Id { get; set; }
    public string Canal { get; set; } = string.Empty;
    public bool Automatico { get; set; }
    public string? Resultado { get; set; }
    public string? Nota { get; set; }
    public string? Autor { get; set; }
    public DateTime CreatedAt { get; set; }
}

/// <summary>
/// Pedir la lista de útiles a los colegios: datos de contacto, seguimiento, mensajes y correo.
/// Lo usan el panel (ContactoColegiosController), la página pública del colegio y los recordatorios automáticos.
/// </summary>
public class ContactoColegiosService
{
    private readonly IDbConnectionFactory _db;
    private readonly IEmailSender _correo;
    private readonly IConfiguration _config;

    public ContactoColegiosService(IDbConnectionFactory db, IEmailSender correo, IConfiguration config)
    {
        _db = db;
        _correo = correo;
        _config = config;
    }

    public bool CorreoConfigurado => _correo.Configurado;
    public string? Remitente => _correo.Remitente;

    /// <summary>Dirección de la tienda (para armar los enlaces de los colegios).</summary>
    public string WebUrl => (_config["App:WebUrl"] ?? "https://ready-web-gamma.vercel.app").TrimEnd('/');

    /// <summary>Año escolar que se está preparando (desde octubre, el siguiente).</summary>
    public static int AnioEscolar(DateTime? hoy = null)
    {
        var d = hoy ?? DateTime.UtcNow.AddHours(-5);
        return d.Month >= 10 ? d.Year + 1 : d.Year;
    }

    private const string SelectColegio = @"
        SELECT s.id AS Id, s.name AS Name, s.address AS Address, s.gestion AS Gestion,
               s.telefono AS Telefono, s.whatsapp AS Whatsapp, s.correo AS Correo,
               s.contacto_nombre AS ContactoNombre, s.contacto_notas AS ContactoNotas, s.contacto_estado AS ContactoEstado,
               s.ultimo_contacto_at AS UltimoContactoAt, s.proximo_contacto_at AS ProximoContactoAt,
               s.recordatorios AS Recordatorios, s.enlace_token AS EnlaceToken,
               (SELECT COUNT(*) FROM supply_lists l WHERE l.school_id = s.id AND l.copia_de IS NULL AND l.year = @Anio
                  AND (l.es_oficial OR l.estado IN ('VALIDADA','PROCESADA')))::int AS ListasAprobadas,
               (SELECT COUNT(*) FROM supply_lists l WHERE l.school_id = s.id AND l.copia_de IS NULL AND l.year = @Anio
                  AND l.estado IN ('PENDIENTE_REVISION','EN_REVISION'))::int AS ListasPorRevisar,
               (SELECT string_agg(DISTINCT x.group_name, ', ') FROM school_sections ss JOIN sections x ON x.id = ss.section_id
                  WHERE ss.school_id = s.id) AS Niveles
        FROM schools s";

    public async Task<IEnumerable<ColegioContacto>> Listar()
    {
        using var conn = _db.CreateConnection();
        return await conn.QueryAsync<ColegioContacto>($"{SelectColegio} ORDER BY s.name", new { Anio = AnioEscolar() });
    }

    public async Task<ColegioContacto?> Obtener(Guid id)
    {
        using var conn = _db.CreateConnection();
        return await conn.QueryFirstOrDefaultAsync<ColegioContacto>($"{SelectColegio} WHERE s.id = @Id", new { Id = id, Anio = AnioEscolar() });
    }

    public async Task<ColegioContacto?> ObtenerPorToken(string token)
    {
        if (string.IsNullOrWhiteSpace(token) || token.Length > 40) return null;
        using var conn = _db.CreateConnection();
        return await conn.QueryFirstOrDefaultAsync<ColegioContacto>($"{SelectColegio} WHERE s.enlace_token = @Token", new { Token = token, Anio = AnioEscolar() });
    }

    /// <summary>Token del enlace del colegio (lo crea la primera vez). Imposible de adivinar: 128 bits aleatorios.</summary>
    public async Task<string> AsegurarToken(Guid schoolId)
    {
        using var conn = _db.CreateConnection();
        var actual = await conn.ExecuteScalarAsync<string?>("SELECT enlace_token FROM schools WHERE id = @Id", new { Id = schoolId });
        if (!string.IsNullOrEmpty(actual)) return actual;
        var nuevo = Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
        // Si dos pedidos llegan a la vez, se queda el primero
        await conn.ExecuteAsync("UPDATE schools SET enlace_token = @Token WHERE id = @Id AND enlace_token IS NULL", new { Id = schoolId, Token = nuevo });
        return await conn.ExecuteScalarAsync<string>("SELECT enlace_token FROM schools WHERE id = @Id", new { Id = schoolId }) ?? nuevo;
    }

    public string Enlace(string token) => $"{WebUrl}/colegio/{token}";

    public async Task<ConfigContacto> LeerConfig()
    {
        using var conn = _db.CreateConnection();
        var json = await conn.ExecuteScalarAsync<string?>("SELECT data::text FROM contacto_colegios_config WHERE id = 1");
        var c = string.IsNullOrWhiteSpace(json) || json == "{}" ? null : JsonSerializer.Deserialize<ConfigContacto>(json, Validacion.Json);
        return c ?? new ConfigContacto();
    }

    public async Task GuardarConfig(ConfigContacto c)
    {
        using var conn = _db.CreateConnection();
        await conn.ExecuteAsync(@"
            INSERT INTO contacto_colegios_config (id, data, updated_at) VALUES (1, @Data::jsonb, NOW())
            ON CONFLICT (id) DO UPDATE SET data = EXCLUDED.data, updated_at = NOW()",
            new { Data = JsonSerializer.Serialize(c, Validacion.Json) });
    }

    /// <summary>Reemplaza {colegio}, {enlace}, {anio} y {contacto} en un texto.</summary>
    public static string Armar(string plantilla, ColegioContacto c, string enlace) => plantilla
        .Replace("{colegio}", c.Name)
        .Replace("{enlace}", enlace)
        .Replace("{anio}", AnioEscolar().ToString())
        .Replace("{contacto}", string.IsNullOrWhiteSpace(c.ContactoNombre) ? "equipo directivo" : c.ContactoNombre.Trim());

    /// <summary>
    /// Anota un contacto y actualiza el seguimiento: si estaba sin contactar pasa a contactado,
    /// y el próximo recordatorio queda para dentro de N días (salvo que ya tenga lista o no le interese).
    /// </summary>
    public async Task Registrar(Guid schoolId, string canal, bool automatico, string? resultado, string? nota, Guid? autor,
                                string? nuevoEstado = null, bool esRecordatorio = false)
    {
        var cfg = await LeerConfig();
        using var conn = _db.CreateConnection();
        conn.Open();
        using var tx = conn.BeginTransaction();
        await conn.ExecuteAsync(@"
            INSERT INTO school_contact_log (school_id, canal, automatico, resultado, nota, created_by)
            VALUES (@SchoolId, @Canal, @Automatico, @Resultado, @Nota, @Autor)",
            new { SchoolId = schoolId, Canal = canal, Automatico = automatico, Resultado = resultado, Nota = nota, Autor = autor }, tx);

        var esContacto = canal is CanalesContacto.WhatsApp or CanalesContacto.Correo or CanalesContacto.Llamada;
        await conn.ExecuteAsync(@"
            UPDATE schools SET
                contacto_estado = COALESCE(@Estado, CASE WHEN @EsContacto AND contacto_estado = 'SIN_CONTACTAR' THEN 'CONTACTADO' ELSE contacto_estado END),
                ultimo_contacto_at = CASE WHEN @EsContacto THEN NOW() ELSE ultimo_contacto_at END,
                recordatorios = recordatorios + CASE WHEN @EsRecordatorio THEN 1 ELSE 0 END,
                proximo_contacto_at = CASE
                    WHEN COALESCE(@Estado, contacto_estado) IN ('LISTA_RECIBIDA', 'NO_INTERESADO') THEN NULL
                    WHEN @EsContacto THEN NOW() + make_interval(days => @Dias)
                    ELSE proximo_contacto_at END
            WHERE id = @SchoolId",
            new { SchoolId = schoolId, Estado = nuevoEstado, EsContacto = esContacto, EsRecordatorio = esRecordatorio, Dias = cfg.DiasRecordatorio }, tx);
        tx.Commit();
    }

    public async Task<IEnumerable<RegistroContacto>> Historial(Guid schoolId)
    {
        using var conn = _db.CreateConnection();
        return await conn.QueryAsync<RegistroContacto>(@"
            SELECT l.id AS Id, l.canal AS Canal, l.automatico AS Automatico, l.resultado AS Resultado, l.nota AS Nota,
                   u.name AS Autor, l.created_at AS CreatedAt
            FROM school_contact_log l LEFT JOIN users u ON u.id = l.created_by
            WHERE l.school_id = @Id ORDER BY l.created_at DESC LIMIT 100", new { Id = schoolId });
    }

    /// <summary>Envía por correo el pedido de lista (o el recordatorio) y lo anota en el historial.</summary>
    public async Task<ResultadoCorreo> EnviarCorreo(ColegioContacto c, bool recordatorio, Guid? autor, bool automatico, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(c.Correo)) return new ResultadoCorreo(false, "El colegio no tiene correo");
        var cfg = await LeerConfig();
        var enlace = Enlace(await AsegurarToken(c.Id));
        var texto = Armar(recordatorio ? cfg.Recordatorio : cfg.Mensaje, c, enlace);
        var asunto = Armar(cfg.Asunto, c, enlace);
        var r = await _correo.Enviar(c.Correo, asunto, texto, ct);
        if (r.Ok)
            await Registrar(c.Id, CanalesContacto.Correo, automatico, recordatorio ? "RECORDATORIO" : "ENVIADO", null, autor, esRecordatorio: recordatorio);
        else if (automatico)
        {
            // Se anota el error y se vuelve a intentar mañana (no cada hora)
            await Registrar(c.Id, CanalesContacto.Nota, true, "ERROR_CORREO", r.Error, null);
            using var conn = _db.CreateConnection();
            await conn.ExecuteAsync("UPDATE schools SET proximo_contacto_at = NOW() + interval '1 day' WHERE id = @Id", new { c.Id });
        }
        return r;
    }

    /// <summary>Colegios a los que toca recordarles por correo (contactados, sin lista y con plazo vencido).</summary>
    public async Task<IEnumerable<ColegioContacto>> PendientesDeRecordatorio(int maxRecordatorios)
    {
        using var conn = _db.CreateConnection();
        return await conn.QueryAsync<ColegioContacto>($@"{SelectColegio}
            WHERE s.contacto_estado IN ('CONTACTADO', 'RESPONDIO') AND s.correo IS NOT NULL AND s.correo <> ''
              AND s.proximo_contacto_at IS NOT NULL AND s.proximo_contacto_at <= NOW() AND s.recordatorios < @Max
            ORDER BY s.proximo_contacto_at LIMIT 50", new { Max = maxRecordatorios, Anio = AnioEscolar() });
    }
}

/// <summary>
/// Recordatorios automáticos por correo: cada hora revisa a qué colegios toca escribirles de nuevo.
/// Solo actúa si el correo está configurado y los recordatorios automáticos están activos.
/// </summary>
public class RecordatoriosColegiosService : BackgroundService
{
    private readonly IServiceScopeFactory _scopes;
    private readonly ILogger<RecordatoriosColegiosService> _logger;
    private static readonly TimeSpan Intervalo = TimeSpan.FromHours(1);

    public RecordatoriosColegiosService(IServiceScopeFactory scopes, ILogger<RecordatoriosColegiosService> logger)
    {
        _scopes = scopes;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Espera a que la API termine de arrancar (y de crear las tablas)
        try { await Task.Delay(TimeSpan.FromMinutes(2), stoppingToken); } catch (TaskCanceledException) { return; }
        using var reloj = new PeriodicTimer(Intervalo);
        do
        {
            try { await Revisar(stoppingToken); }
            catch (Exception ex) when (ex is not OperationCanceledException) { _logger.LogError(ex, "Falló la revisión de recordatorios a colegios"); }
        } while (await reloj.WaitForNextTickAsync(stoppingToken));
    }

    public async Task<int> Revisar(CancellationToken ct)
    {
        using var scope = _scopes.CreateScope();
        var svc = scope.ServiceProvider.GetRequiredService<ContactoColegiosService>();
        if (!svc.CorreoConfigurado) return 0;
        var cfg = await svc.LeerConfig();
        if (!cfg.RecordatoriosAutomaticos || cfg.MaxRecordatorios == 0) return 0;

        var enviados = 0;
        foreach (var c in await svc.PendientesDeRecordatorio(cfg.MaxRecordatorios))
        {
            if (ct.IsCancellationRequested) break;
            var r = await svc.EnviarCorreo(c, recordatorio: true, autor: null, automatico: true, ct);
            if (r.Ok) enviados++;
            await Task.Delay(TimeSpan.FromSeconds(1), ct); // sin ráfagas al proveedor de correo
        }
        if (enviados > 0) _logger.LogInformation("Recordatorios a colegios enviados: {Cantidad}", enviados);
        return enviados;
    }
}
