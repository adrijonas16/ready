using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using UtilesApi.DTOs;
using UtilesApi.Infrastructure.Database;
using UtilesApi.Infrastructure.Security;
using UtilesApi.Services;

namespace UtilesApi.Controllers;

/// <summary>
/// Consultas de producto: el cliente pregunta si podemos conseguir algo que no está en la tienda;
/// la tienda averigua y responde sí o no dentro de las 24 horas.
/// </summary>
[ApiController]
[Route("api/consultas")]
public class ConsultasController : ControllerBase
{
    public const int MaxProducto = 200;
    public const int MaxDetalle = 1000;
    public const int MaxNombre = 120;
    public const int MaxContacto = 150;
    public const int MaxRespuesta = 1000;
    /// <summary>Consultas sin responder que puede tener abiertas una misma persona.</summary>
    public const int MaxPendientes = 10;
    public static readonly TimeSpan Plazo = TimeSpan.FromHours(24);

    private readonly ConsultaRepository _repo;
    private readonly INotificationService _notifications;
    private readonly ILogger<ConsultasController> _logger;

    public ConsultasController(ConsultaRepository repo, INotificationService notifications, ILogger<ConsultasController> logger)
    {
        _repo = repo;
        _notifications = notifications;
        _logger = logger;
    }

    private static ConsultaResponse Mapear(Consulta c, bool paraPanel) => new()
    {
        Id = c.Id,
        Producto = c.Producto,
        Detalle = c.Detalle,
        Estado = c.Estado,
        Respuesta = c.Respuesta,
        CreatedAt = c.CreatedAt,
        VenceAt = c.CreatedAt + Plazo,
        RespondidaAt = c.RespondidaAt,
        SupplyListId = c.SupplyListId,
        Nombre = paraPanel ? c.Nombre ?? c.UserName : null,
        Contacto = paraPanel ? c.Contacto : null,
        UserEmail = paraPanel ? c.UserEmail : null,
        UserPhone = paraPanel ? c.UserPhone : null,
    };

    private static string? Limpiar(string? texto) => string.IsNullOrWhiteSpace(texto) ? null : texto.Trim();

    /// <summary>Correo o celular peruano; devuelve el contacto normalizado o null si no sirve para avisar.</summary>
    private static string? ContactoValido(string? contacto)
    {
        if (contacto == null) return null;
        if (contacto.Contains('@')) return Validacion.CorreoValido(contacto) ? contacto.Trim().ToLowerInvariant() : null;
        return Validacion.CelularPeru(contacto);
    }

    /// <summary>Nueva consulta. Pública: sin sesión pide nombre y un correo o celular para avisar.</summary>
    [HttpPost]
    [AllowAnonymous]
    [EnableRateLimiting(RateLimits.Consultas)]
    [Honeypot]
    public async Task<ActionResult<ApiResponse<ConsultaResponse>>> Crear([FromBody] CrearConsultaRequest req)
    {
        var producto = Limpiar(req.Producto);
        var detalle = Limpiar(req.Detalle);
        var nombre = Limpiar(req.Nombre);
        var contacto = Limpiar(req.Contacto);
        var userId = User.GetUserId();

        if (producto == null)
            return BadRequest(ApiResponse<ConsultaResponse>.Fail("INVALID", "Escribe qué producto buscas"));
        if (producto.Length > MaxProducto || !Validacion.Largo(detalle, MaxDetalle) || !Validacion.Largo(nombre, MaxNombre) || !Validacion.Largo(contacto, MaxContacto))
            return BadRequest(ApiResponse<ConsultaResponse>.Fail("INVALID", $"El producto puede tener hasta {MaxProducto} caracteres y el detalle hasta {MaxDetalle}"));
        if (!Validacion.SinControl(producto) || !Validacion.SinControl(detalle) || !Validacion.SinControl(nombre) || !Validacion.SinControl(contacto))
            return BadRequest(ApiResponse<ConsultaResponse>.Fail("INVALID", "El texto tiene caracteres no permitidos"));

        if (userId == null)
        {
            if (nombre == null)
                return BadRequest(ApiResponse<ConsultaResponse>.Fail("INVALID", "Escribe tu nombre"));
            contacto = ContactoValido(contacto);
            if (contacto == null)
                return BadRequest(ApiResponse<ConsultaResponse>.Fail("INVALID", "Escribe un correo o un celular (9 dígitos) para avisarte"));
        }
        else if (contacto != null)
        {
            // Con sesión el contacto es opcional (ya tenemos su correo), pero si lo da debe servir
            contacto = ContactoValido(contacto);
            if (contacto == null)
                return BadRequest(ApiResponse<ConsultaResponse>.Fail("INVALID", "El contacto debe ser un correo o un celular de 9 dígitos"));
        }

        if (await _repo.CountPendientesDelUsuario(userId, contacto) >= MaxPendientes)
            return BadRequest(ApiResponse<ConsultaResponse>.Fail("TOO_MANY", "Ya tienes varias consultas sin responder. Espera nuestra respuesta."));

        var id = await _repo.Create(new Consulta
        {
            UserId = userId, SupplyListId = req.SupplyListId, Producto = producto, Detalle = detalle, Nombre = nombre, Contacto = contacto
        });
        var creada = (await _repo.GetById(id))!;

        try { await _notifications.NotifyNewInquiry(id.ToString(), producto, nombre ?? creada.UserName ?? "Cliente"); }
        catch (Exception ex) { _logger.LogWarning(ex, "No se pudo avisar de la consulta {Id}", id); }

        return Ok(ApiResponse<ConsultaResponse>.Ok(Mapear(creada, paraPanel: false)));
    }

    /// <summary>Consultas del cliente con sesión.</summary>
    [HttpGet("mias")]
    [Authorize]
    public async Task<ActionResult<ApiResponse<IEnumerable<ConsultaResponse>>>> Mias()
    {
        var userId = User.GetUserId();
        if (userId == null) return Unauthorized(ApiResponse<IEnumerable<ConsultaResponse>>.Fail("UNAUTHORIZED", "Inicia sesión"));
        var lista = await _repo.GetByUser(userId.Value);
        return Ok(ApiResponse<IEnumerable<ConsultaResponse>>.Ok(lista.Select(c => Mapear(c, paraPanel: false))));
    }

    /// <summary>
    /// Una consulta por id (para quien la hizo sin sesión: el id solo lo tiene su navegador).
    /// Si la consulta es de una cuenta, solo la ve su dueño o la tienda.
    /// </summary>
    [HttpGet("{id:guid}")]
    [AllowAnonymous]
    public async Task<ActionResult<ApiResponse<ConsultaResponse>>> GetById(Guid id)
    {
        var c = await _repo.GetById(id);
        if (c == null) return NotFound(ApiResponse<ConsultaResponse>.Fail("NOT_FOUND", "Consulta no encontrada"));
        var staff = User.IsStaff();
        if (c.UserId.HasValue && !staff && User.GetUserId() != c.UserId)
            return NotFound(ApiResponse<ConsultaResponse>.Fail("NOT_FOUND", "Consulta no encontrada"));
        return Ok(ApiResponse<ConsultaResponse>.Ok(Mapear(c, paraPanel: staff)));
    }

    /// <summary>Panel: todas las consultas (filtro opcional por estado).</summary>
    [HttpGet]
    [Authorize(Roles = AppRoles.Staff)]
    public async Task<ActionResult<ApiResponse<IEnumerable<ConsultaResponse>>>> GetAll([FromQuery] string? estado)
    {
        if (estado != null && !EstadosConsulta.Todos.Contains(estado))
            return BadRequest(ApiResponse<IEnumerable<ConsultaResponse>>.Fail("INVALID", "Estado inválido: PENDIENTE, DISPONIBLE o NO_DISPONIBLE"));
        var lista = await _repo.GetAll(estado);
        return Ok(ApiResponse<IEnumerable<ConsultaResponse>>.Ok(lista.Select(c => Mapear(c, paraPanel: true))));
    }

    /// <summary>Panel: responder sí o no (se puede corregir después). Avisa al cliente si tiene cuenta.</summary>
    [HttpPut("{id:guid}/respuesta")]
    [Authorize(Roles = AppRoles.Staff)]
    public async Task<ActionResult<ApiResponse<ConsultaResponse>>> Responder(Guid id, [FromBody] ResponderConsultaRequest req)
    {
        if (req.Disponible == null)
            return BadRequest(ApiResponse<ConsultaResponse>.Fail("INVALID", "Indica si lo conseguimos o no"));
        var respuesta = Limpiar(req.Respuesta);
        if (!Validacion.Largo(respuesta, MaxRespuesta) || !Validacion.SinControl(respuesta))
            return BadRequest(ApiResponse<ConsultaResponse>.Fail("INVALID", $"La respuesta puede tener hasta {MaxRespuesta} caracteres"));

        var estado = req.Disponible.Value ? EstadosConsulta.Disponible : EstadosConsulta.NoDisponible;
        if (!await _repo.Responder(id, estado, respuesta, User.GetUserId()))
            return NotFound(ApiResponse<ConsultaResponse>.Fail("NOT_FOUND", "Consulta no encontrada"));

        var c = (await _repo.GetById(id))!;
        if (c.UserId.HasValue)
        {
            try { await _notifications.NotifyInquiryAnswered(c.UserId.Value.ToString(), id.ToString(), c.Producto, req.Disponible.Value); }
            catch (Exception ex) { _logger.LogWarning(ex, "No se pudo avisar la respuesta de la consulta {Id}", id); }
        }
        return Ok(ApiResponse<ConsultaResponse>.Ok(Mapear(c, paraPanel: true)));
    }
}
