using Dapper;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using UtilesApi.DTOs;
using UtilesApi.Infrastructure.Database;
using UtilesApi.Services;

namespace UtilesApi.Controllers;

/// <summary>
/// Admin > Pedir listas: contactos de cada colegio, seguimiento, mensajes por WhatsApp/correo/llamada,
/// envío de correos por lotes y configuración del mensaje y los recordatorios.
/// </summary>
[ApiController]
[Route("api/contacto-colegios")]
[Authorize(Roles = AppRoles.Staff)]
public class ContactoColegiosController : ControllerBase
{
    private const int MaxNotas = 2000;
    private const int MaxLote = 200;

    private readonly ContactoColegiosService _svc;
    private readonly IDbConnectionFactory _db;

    public ContactoColegiosController(ContactoColegiosService svc, IDbConnectionFactory db)
    {
        _svc = svc;
        _db = db;
    }

    private ColegioContactoResponse Mapear(ColegioContacto c) => new()
    {
        Id = c.Id,
        Nombre = c.Name,
        Direccion = c.Address,
        Gestion = c.Gestion,
        Departamento = c.Departamento,
        Provincia = c.Provincia,
        Distrito = c.Distrito,
        Niveles = c.Niveles,
        Telefono = c.Telefono,
        Whatsapp = c.Whatsapp,
        Correo = c.Correo,
        ContactoNombre = c.ContactoNombre,
        Notas = c.ContactoNotas,
        Estado = c.ContactoEstado,
        UltimoContactoAt = c.UltimoContactoAt,
        ProximoContactoAt = c.ProximoContactoAt,
        Recordatorios = c.Recordatorios,
        ListasAprobadas = c.ListasAprobadas,
        ListasPorRevisar = c.ListasPorRevisar,
        Enlace = c.EnlaceToken == null ? null : _svc.Enlace(c.EnlaceToken),
    };

    [HttpGet]
    public async Task<ActionResult<ApiResponse<IEnumerable<ColegioContactoResponse>>>> Listar()
    {
        var lista = await _svc.Listar();
        return Ok(ApiResponse<IEnumerable<ColegioContactoResponse>>.Ok(lista.Select(Mapear)));
    }

    /// <summary>Datos de contacto, notas y (opcional) estado del seguimiento.</summary>
    [HttpPut("{id:guid}")]
    public async Task<ActionResult<ApiResponse<ColegioContactoResponse>>> Actualizar(Guid id, [FromBody] ActualizarContactoRequest req)
    {
        static string? Limpio(string? v) => string.IsNullOrWhiteSpace(v) ? null : v.Trim();
        var telefono = Limpio(req.Telefono);
        var whatsapp = Limpio(req.Whatsapp);
        var correo = Limpio(req.Correo)?.ToLowerInvariant();
        var nombre = Limpio(req.ContactoNombre);
        var notas = Limpio(req.Notas);

        if (telefono != null && !Validacion.TelefonoValido(telefono))
            return BadRequest(ApiResponse<ColegioContactoResponse>.Fail("INVALID", "El teléfono no es válido (solo números, espacios, + y -)"));
        if (whatsapp != null)
        {
            var cel = Validacion.CelularPeru(whatsapp);
            if (cel == null) return BadRequest(ApiResponse<ColegioContactoResponse>.Fail("INVALID", "El WhatsApp debe ser un celular de 9 dígitos que empiece con 9"));
            whatsapp = cel;
        }
        if (correo != null && !Validacion.CorreoValido(correo))
            return BadRequest(ApiResponse<ColegioContactoResponse>.Fail("INVALID", "El correo no es válido"));
        if (!Validacion.Largo(nombre, 120) || !Validacion.Largo(notas, MaxNotas) || !Validacion.SinControl(nombre) || !Validacion.SinControl(notas))
            return BadRequest(ApiResponse<ColegioContactoResponse>.Fail("INVALID", $"El nombre puede tener hasta 120 caracteres y las notas hasta {MaxNotas}"));
        if (req.Estado != null && !EstadosContacto.Todos.Contains(req.Estado))
            return BadRequest(ApiResponse<ColegioContactoResponse>.Fail("INVALID", "Estado no válido"));

        using var conn = _db.CreateConnection();
        var filas = await conn.ExecuteAsync(@"
            UPDATE schools SET telefono = @Telefono, whatsapp = @Whatsapp, correo = @Correo,
                               contacto_nombre = @Nombre, contacto_notas = @Notas,
                               contacto_estado = COALESCE(@Estado, contacto_estado),
                               proximo_contacto_at = CASE WHEN @Estado IN ('LISTA_RECIBIDA', 'NO_INTERESADO') THEN NULL ELSE proximo_contacto_at END
            WHERE id = @Id",
            new { Id = id, Telefono = telefono, Whatsapp = whatsapp, Correo = correo, Nombre = nombre, Notas = notas, req.Estado });
        if (filas == 0) return NotFound(ApiResponse<ColegioContactoResponse>.Fail("NOT_FOUND", "Colegio no encontrado"));
        return Ok(ApiResponse<ColegioContactoResponse>.Ok(Mapear((await _svc.Obtener(id))!)));
    }

    /// <summary>Enlace propio del colegio (se crea la primera vez) y los mensajes listos para enviar.</summary>
    [HttpPost("{id:guid}/mensaje")]
    public async Task<ActionResult<ApiResponse<MensajeColegioResponse>>> Mensaje(Guid id)
    {
        var c = await _svc.Obtener(id);
        if (c == null) return NotFound(ApiResponse<MensajeColegioResponse>.Fail("NOT_FOUND", "Colegio no encontrado"));
        var enlace = _svc.Enlace(await _svc.AsegurarToken(id));
        var cfg = await _svc.LeerConfig();
        return Ok(ApiResponse<MensajeColegioResponse>.Ok(new MensajeColegioResponse
        {
            Enlace = enlace,
            Mensaje = ContactoColegiosService.Armar(cfg.Mensaje, c, enlace),
            Recordatorio = ContactoColegiosService.Armar(cfg.Recordatorio, c, enlace),
            Asunto = ContactoColegiosService.Armar(cfg.Asunto, c, enlace),
        }));
    }

    /// <summary>Anota un contacto hecho a mano (WhatsApp enviado, llamada, nota) y, si se indica, cambia el estado.</summary>
    [HttpPost("{id:guid}/registro")]
    public async Task<ActionResult<ApiResponse<ColegioContactoResponse>>> Registrar(Guid id, [FromBody] RegistrarContactoRequest req)
    {
        if (req.Canal == null || !CanalesContacto.Manuales.Contains(req.Canal))
            return BadRequest(ApiResponse<ColegioContactoResponse>.Fail("INVALID", "Canal no válido: WHATSAPP, CORREO, LLAMADA o NOTA"));
        if (req.Estado != null && !EstadosContacto.Todos.Contains(req.Estado))
            return BadRequest(ApiResponse<ColegioContactoResponse>.Fail("INVALID", "Estado no válido"));
        var nota = string.IsNullOrWhiteSpace(req.Nota) ? null : req.Nota.Trim();
        var resultado = string.IsNullOrWhiteSpace(req.Resultado) ? null : req.Resultado.Trim();
        if (!Validacion.Largo(nota, MaxNotas) || !Validacion.Largo(resultado, 30) || !Validacion.SinControl(nota) || !Validacion.SinControl(resultado))
            return BadRequest(ApiResponse<ColegioContactoResponse>.Fail("INVALID", $"La nota puede tener hasta {MaxNotas} caracteres"));
        if (await _svc.Obtener(id) == null) return NotFound(ApiResponse<ColegioContactoResponse>.Fail("NOT_FOUND", "Colegio no encontrado"));

        await _svc.Registrar(id, req.Canal, false, resultado, nota, User.GetUserId(), req.Estado, esRecordatorio: req.EsRecordatorio);
        return Ok(ApiResponse<ColegioContactoResponse>.Ok(Mapear((await _svc.Obtener(id))!)));
    }

    [HttpGet("{id:guid}/historial")]
    public async Task<ActionResult<ApiResponse<IEnumerable<RegistroContacto>>>> Historial(Guid id) =>
        Ok(ApiResponse<IEnumerable<RegistroContacto>>.Ok(await _svc.Historial(id)));

    /// <summary>Envía el pedido de lista por correo a varios colegios (solo a los que tienen correo).</summary>
    [HttpPost("correo")]
    public async Task<ActionResult<ApiResponse<EnvioCorreoResponse>>> EnviarCorreos([FromBody] EnviarCorreosRequest req, CancellationToken ct)
    {
        if (!_svc.CorreoConfigurado)
            return StatusCode(503, ApiResponse<EnvioCorreoResponse>.Fail("EMAIL_NOT_CONFIGURED",
                "El envío de correos aún no está configurado. Falta la clave del servicio de correo."));
        var ids = (req.Ids ?? new()).Distinct().ToList();
        if (ids.Count == 0) return BadRequest(ApiResponse<EnvioCorreoResponse>.Fail("INVALID", "Elige al menos un colegio"));
        if (ids.Count > MaxLote) return BadRequest(ApiResponse<EnvioCorreoResponse>.Fail("INVALID", $"Máximo {MaxLote} colegios por envío"));

        var r = new EnvioCorreoResponse();
        foreach (var id in ids)
        {
            var c = await _svc.Obtener(id);
            if (c == null) continue;
            if (string.IsNullOrWhiteSpace(c.Correo)) { r.SinCorreo.Add(c.Name); continue; }
            if (c.ContactoEstado == EstadosContacto.NoInteresado) { r.Omitidos.Add(c.Name); continue; }
            var envio = await _svc.EnviarCorreo(c, req.Recordatorio, User.GetUserId(), automatico: false, ct);
            if (envio.Ok) r.Enviados++;
            else r.Fallidos.Add($"{c.Name}: {envio.Error}");
        }
        return Ok(ApiResponse<EnvioCorreoResponse>.Ok(r));
    }

    /// <summary>Envía ya los recordatorios por correo que estén pendientes (lo mismo que hace solo cada hora).</summary>
    [HttpPost("recordatorios")]
    public async Task<ActionResult<ApiResponse<int>>> EnviarRecordatoriosPendientes([FromServices] RecordatoriosColegiosService recordatorios, CancellationToken ct)
    {
        if (!_svc.CorreoConfigurado)
            return StatusCode(503, ApiResponse<int>.Fail("EMAIL_NOT_CONFIGURED", "El envío de correos aún no está configurado."));
        return Ok(ApiResponse<int>.Ok(await recordatorios.Revisar(ct)));
    }

    [HttpGet("config")]
    public async Task<ActionResult<ApiResponse<ConfigContactoResponse>>> LeerConfig() =>
        Ok(ApiResponse<ConfigContactoResponse>.Ok(new ConfigContactoResponse
        {
            Config = await _svc.LeerConfig(),
            CorreoConfigurado = _svc.CorreoConfigurado,
            Remitente = _svc.Remitente,
        }));

    [HttpPut("config")]
    public async Task<ActionResult<ApiResponse<ConfigContactoResponse>>> GuardarConfig([FromBody] ConfigContacto cfg)
    {
        cfg.Mensaje = cfg.Mensaje?.Trim() ?? "";
        cfg.Recordatorio = cfg.Recordatorio?.Trim() ?? "";
        cfg.Asunto = cfg.Asunto?.Trim() ?? "";
        var error = cfg.Validar();
        if (error != null) return BadRequest(ApiResponse<ConfigContactoResponse>.Fail("INVALID", error));
        await _svc.GuardarConfig(cfg);
        return await LeerConfig();
    }
}

public class ActualizarContactoRequest
{
    public string? Telefono { get; set; }
    public string? Whatsapp { get; set; }
    public string? Correo { get; set; }
    public string? ContactoNombre { get; set; }
    public string? Notas { get; set; }
    public string? Estado { get; set; }
}

public class RegistrarContactoRequest
{
    public string? Canal { get; set; }
    public string? Resultado { get; set; }
    public string? Nota { get; set; }
    public string? Estado { get; set; }
    public bool EsRecordatorio { get; set; }
}

public class EnviarCorreosRequest
{
    public List<Guid>? Ids { get; set; }
    public bool Recordatorio { get; set; }
}

public class EnvioCorreoResponse
{
    public int Enviados { get; set; }
    public List<string> SinCorreo { get; set; } = new();
    public List<string> Omitidos { get; set; } = new();
    public List<string> Fallidos { get; set; } = new();
}

public class MensajeColegioResponse
{
    public string Enlace { get; set; } = string.Empty;
    public string Mensaje { get; set; } = string.Empty;
    public string Recordatorio { get; set; } = string.Empty;
    public string Asunto { get; set; } = string.Empty;
}

public class ConfigContactoResponse
{
    public ConfigContacto Config { get; set; } = new();
    public bool CorreoConfigurado { get; set; }
    public string? Remitente { get; set; }
}

public class ColegioContactoResponse
{
    public Guid Id { get; set; }
    public string Nombre { get; set; } = string.Empty;
    public string? Direccion { get; set; }
    public string? Gestion { get; set; }
    public string Departamento { get; set; } = Cobertura.Base;
    public string? Provincia { get; set; }
    public string? Distrito { get; set; }
    public string? Niveles { get; set; }
    public string? Telefono { get; set; }
    public string? Whatsapp { get; set; }
    public string? Correo { get; set; }
    public string? ContactoNombre { get; set; }
    public string? Notas { get; set; }
    public string Estado { get; set; } = string.Empty;
    public DateTime? UltimoContactoAt { get; set; }
    public DateTime? ProximoContactoAt { get; set; }
    public int Recordatorios { get; set; }
    public int ListasAprobadas { get; set; }
    public int ListasPorRevisar { get; set; }
    public string? Enlace { get; set; }
}
