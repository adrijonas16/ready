using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using UtilesApi.DTOs;
using UtilesApi.Services;
using UtilesApi.Infrastructure.Database;
using System.Security.Claims;
using Microsoft.IdentityModel.Tokens;
using System.IdentityModel.Tokens.Jwt;
using System.Text;
using Microsoft.AspNetCore.RateLimiting;
using UtilesApi.Infrastructure.Security;

namespace UtilesApi.Controllers;

[ApiController]
[Route("api/[controller]")]
public class AuthController : ControllerBase
{
    public const int MinPassword = 8;
    public const int MaxPassword = 128;
    public const int MinNombre = 2;
    public const int MaxNombre = 100;

    private readonly IAuthService _authService;
    private readonly UserRepository _userRepo;
    private readonly LoginGuard _loginGuard;
    private readonly ILogger<AuthController> _logger;

    public AuthController(IAuthService authService, UserRepository userRepo, LoginGuard loginGuard, ILogger<AuthController> logger)
    {
        _authService = authService;
        _userRepo = userRepo;
        _loginGuard = loginGuard;
        _logger = logger;
    }

    private static string? ValidarNombre(string? nombre)
    {
        var n = nombre?.Trim() ?? "";
        if (n.Length < MinNombre) return "Escribe tu nombre";
        if (n.Length > MaxNombre) return $"El nombre puede tener hasta {MaxNombre} caracteres";
        if (!Validacion.SinControl(n) || n.Contains('<') || n.Contains('>')) return "El nombre tiene caracteres no permitidos";
        return null;
    }

    [HttpPost("register")]
    [AllowAnonymous]
    [EnableRateLimiting(RateLimits.Register)]
    [Honeypot]
    public async Task<ActionResult<ApiResponse<AuthResponse>>> Register([FromBody] UserRegisterRequest request)
    {
        if (!Validacion.CorreoValido(request.Email))
            return BadRequest(ApiResponse<AuthResponse>.Fail("INVALID_EMAIL", "Escribe un correo válido, por ejemplo nombre@correo.com"));
        if (string.IsNullOrEmpty(request.Password) || request.Password.Length < MinPassword)
            return BadRequest(ApiResponse<AuthResponse>.Fail("WEAK_PASSWORD", $"La contraseña debe tener al menos {MinPassword} caracteres"));
        if (request.Password.Length > MaxPassword)
            return BadRequest(ApiResponse<AuthResponse>.Fail("WEAK_PASSWORD", $"La contraseña puede tener hasta {MaxPassword} caracteres"));
        var errorNombre = ValidarNombre(request.Name);
        if (errorNombre != null)
            return BadRequest(ApiResponse<AuthResponse>.Fail("INVALID_NAME", errorNombre));

        var user = await _authService.Register(request.Email.Trim(), request.Password, request.Name.Trim());
        if (user == null)
            return BadRequest(ApiResponse<AuthResponse>.Fail("EMAIL_EXISTS", "El email ya esta registrado"));

        var token = _authService.GenerateToken(user);
        return Ok(ApiResponse<AuthResponse>.Ok(new AuthResponse { Token = token, User = UserResponse.From(user) }));
    }

    [HttpPost("login")]
    [AllowAnonymous]
    [EnableRateLimiting(RateLimits.Login)]
    public async Task<ActionResult<ApiResponse<AuthResponse>>> Login([FromBody] UserLoginRequest request)
    {
        // Datos absurdos: ni se consulta la base
        if (string.IsNullOrWhiteSpace(request.Email) || request.Email.Length > 254 ||
            string.IsNullOrEmpty(request.Password) || request.Password.Length > MaxPassword)
            return Unauthorized(ApiResponse<AuthResponse>.Fail("INVALID_CREDENTIALS", "Correo o contraseña incorrectos"));

        // Por correo: 10 intentos por minuto y bloqueo de 15 minutos tras 5 contraseñas incorrectas
        var (permitido, esperar, bloqueado) = _loginGuard.Intentar(request.Email);
        if (!permitido)
        {
            Response.Headers.RetryAfter = esperar.ToString(System.Globalization.CultureInfo.InvariantCulture);
            var minutos = Math.Max(1, (int)Math.Ceiling(esperar / 60.0));
            var mensaje = bloqueado
                ? $"Demasiados intentos fallidos con este correo. Espera {minutos} minuto{(minutos == 1 ? "" : "s")} o restablece tu contraseña."
                : RateLimits.MensajeLimite;
            return StatusCode(StatusCodes.Status429TooManyRequests, ApiResponse<AuthResponse>.Fail("RATE_LIMIT", mensaje));
        }

        var (user, token) = await _authService.Login(request.Email.Trim(), request.Password);
        if (user == null)
        {
            _loginGuard.Fallo(request.Email);
            _logger.LogInformation("Login fallido desde {Ip}", RateLimits.ClientIp(HttpContext));
            return Unauthorized(ApiResponse<AuthResponse>.Fail("INVALID_CREDENTIALS", "Correo o contraseña incorrectos"));
        }
        _loginGuard.Exito(request.Email);

        return Ok(ApiResponse<AuthResponse>.Ok(new AuthResponse { Token = token, User = UserResponse.From(user) }));
    }

    [HttpGet("me")]
    [Authorize]
    public async Task<ActionResult<ApiResponse<UserResponse>>> GetCurrentUser()
    {
        var userId = User.GetUserId();
        if (userId == null)
            return Unauthorized(ApiResponse<UserResponse>.Fail("UNAUTHORIZED", "No autorizado"));

        var user = await _userRepo.GetById(userId.Value);
        if (user == null)
            return NotFound(ApiResponse<UserResponse>.Fail("USER_NOT_FOUND", "Usuario no encontrado"));

        return Ok(ApiResponse<UserResponse>.Ok(UserResponse.From(user)));
    }

    /// <summary>El usuario logueado guarda su nombre, teléfono, dirección y DNI/RUC.</summary>
    [HttpPut("me")]
    [Authorize]
    public async Task<ActionResult<ApiResponse<UserResponse>>> UpdateCurrentUser([FromBody] UpdateProfileRequest request)
    {
        var userId = User.GetUserId();
        if (userId == null)
            return Unauthorized(ApiResponse<UserResponse>.Fail("UNAUTHORIZED", "No autorizado"));

        var user = await _userRepo.GetById(userId.Value);
        if (user == null)
            return NotFound(ApiResponse<UserResponse>.Fail("USER_NOT_FOUND", "Usuario no encontrado"));

        static string? Limpio(string? v) => string.IsNullOrWhiteSpace(v) ? null : v.Trim();

        if (request.Name != null)
        {
            var errorNombre = ValidarNombre(request.Name);
            if (errorNombre != null)
                return BadRequest(ApiResponse<UserResponse>.Fail("INVALID_NAME", errorNombre));
            user.Name = request.Name.Trim();
        }

        var phone = Limpio(request.Phone);
        if (phone != null && Validacion.CelularPeru(phone) == null)
            return BadRequest(ApiResponse<UserResponse>.Fail("INVALID_PHONE", "Escribe un celular de 9 dígitos que empiece con 9 (puedes poner +51)"));
        var documento = Limpio(request.Documento);
        if (documento != null && !Validacion.DniORucValido(documento))
            return BadRequest(ApiResponse<UserResponse>.Fail("INVALID_DOCUMENT", "Escribe un DNI de 8 dígitos o un RUC de 11"));
        var direccion = Limpio(request.Address);
        if (!Validacion.Largo(direccion, 300) || !Validacion.SinControl(direccion))
            return BadRequest(ApiResponse<UserResponse>.Fail("INVALID_ADDRESS", "La dirección puede tener hasta 300 caracteres"));

        user.Phone = phone;
        user.Address = direccion;
        user.Documento = documento;
        await _userRepo.Update(user);

        return Ok(ApiResponse<UserResponse>.Ok(UserResponse.From(user)));
    }

    [HttpGet("users")]
    [Authorize(Roles = AppRoles.Admin)]
    public async Task<IActionResult> GetAllUsers()
    {
        using var conn = HttpContext.RequestServices.GetRequiredService<IDbConnectionFactory>().CreateConnection();
        var users = await Dapper.SqlMapper.QueryAsync<dynamic>(conn, @"
            SELECT id, email, name, phone, address, documento, role::text as role, created_at, created_at AS ""createdAt""
            FROM users ORDER BY created_at DESC");
        return Ok(ApiResponse<object>.Ok(users));
    }

    [HttpPut("users/{id}/role")]
    [Authorize(Roles = AppRoles.Admin)]
    public async Task<IActionResult> UpdateUserRole(Guid id, [FromBody] UpdateRoleRequest req)
    {
        var role = (req.Role ?? "").Trim().ToUpperInvariant();
        if (role != AppRoles.User && role != AppRoles.Admin && role != AppRoles.Operator)
            return BadRequest(ApiResponse<bool>.Fail("INVALID_ROLE", "Rol inválido: usa USER, OPERATOR o ADMIN"));
        if (User.GetUserId() == id)
            return BadRequest(ApiResponse<bool>.Fail("OWN_ROLE", "No puedes cambiar tu propio rol"));

        using var conn = HttpContext.RequestServices.GetRequiredService<IDbConnectionFactory>().CreateConnection();
        var filas = await Dapper.SqlMapper.ExecuteAsync(conn, "UPDATE users SET role = @Role, updated_at = NOW() WHERE id = @Id", new { Id = id, Role = role });
        if (filas == 0) return NotFound(ApiResponse<bool>.Fail("USER_NOT_FOUND", "Usuario no encontrado"));
        return Ok(ApiResponse<bool>.Ok(true));
    }
}

public class UpdateRoleRequest { public string Role { get; set; } = "USER"; }

[ApiController]
[Route("api/schools")]
public class SchoolsController : ControllerBase
{
    private readonly SchoolRepository _schoolRepo;
    private readonly GradeRepository _gradeRepo;
    private readonly SectionRepository _sectionRepo;

    public SchoolsController(SchoolRepository schoolRepo, GradeRepository gradeRepo, SectionRepository sectionRepo)
    {
        _schoolRepo = schoolRepo;
        _gradeRepo = gradeRepo;
        _sectionRepo = sectionRepo;
    }

    /// <summary>
    /// Colegios que ve la tienda: solo los de Arequipa mientras el envío nacional esté apagado.
    /// El personal pide <c>?todos=true</c> (y opcionalmente <c>departamento</c>) para ver y contactar a todos.
    /// </summary>
    [HttpGet]
    public async Task<ActionResult<ApiResponse<IEnumerable<SchoolResponse>>>> GetAll([FromQuery] string? departamento, [FromQuery] bool todos = false)
    {
        string? filtro = null;
        if (!string.IsNullOrWhiteSpace(departamento))
        {
            filtro = Cobertura.Normalizar(departamento);
            if (filtro == null)
                return BadRequest(ApiResponse<IEnumerable<SchoolResponse>>.Fail("VALIDATION", "Departamento no válido"));
        }

        var esPersonal = todos && (User.IsInRole(AppRoles.Admin) || User.IsInRole(AppRoles.Operator));
        if (!esPersonal)
        {
            using var conn = HttpContext.RequestServices.GetRequiredService<IDbConnectionFactory>().CreateConnection();
            var ajustes = await StoreSettings.Leer(conn);
            if (!ajustes.CoberturaNacional)
            {
                if (filtro != null && filtro != Cobertura.Base)
                    return Ok(ApiResponse<IEnumerable<SchoolResponse>>.Ok(Array.Empty<SchoolResponse>()));
                filtro = Cobertura.Base;
            }
        }

        var schools = await _schoolRepo.GetAll(filtro);
        return Ok(ApiResponse<IEnumerable<SchoolResponse>>.Ok(schools.Select(ARespuesta)));
    }

    private static SchoolResponse ARespuesta(Core.Entities.School s) => new()
    {
        Id = s.Id,
        Name = s.Name,
        Address = s.Address,
        Departamento = s.Departamento,
        Provincia = s.Provincia,
        Distrito = s.Distrito
    };

    /// <summary>Departamento válido (null = Arequipa) y largos de provincia/distrito.</summary>
    private static string? ValidarUbicacion(CreateSchoolRequest r, out string departamento)
    {
        departamento = Cobertura.Base;
        if (string.IsNullOrWhiteSpace(r.Name) || r.Name.Trim().Length > 255) return "Escribe el nombre del colegio (hasta 255 caracteres)";
        if (!string.IsNullOrWhiteSpace(r.Departamento))
        {
            var d = Cobertura.Normalizar(r.Departamento);
            if (d == null) return "Elige un departamento válido";
            departamento = d;
        }
        if (!Validacion.Largo(r.Provincia, 80) || !Validacion.Largo(r.Distrito, 80)) return "La provincia y el distrito pueden tener hasta 80 caracteres";
        return null;
    }

    private static string? Limpio(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    [HttpGet("{id}")]
    public async Task<ActionResult<ApiResponse<SchoolResponse>>> GetById(Guid id)
    {
        var school = await _schoolRepo.GetById(id);
        if (school == null)
            return NotFound(ApiResponse<SchoolResponse>.Fail("NOT_FOUND", "Colegio no encontrado"));

        return Ok(ApiResponse<SchoolResponse>.Ok(ARespuesta(school)));
    }

    [HttpGet("{id}/grades")]
    public async Task<ActionResult<ApiResponse<IEnumerable<GradeResponse>>>> GetGrades(Guid id)
    {
        var grades = await _gradeRepo.GetBySchoolId(id);
        return Ok(ApiResponse<IEnumerable<GradeResponse>>.Ok(grades.Select(g => new GradeResponse
        {
            Id = g.Id,
            SchoolId = g.SchoolId,
            Name = g.Name,
            Year = g.Year
        })));
    }

    [HttpPost]
    [Authorize(Roles = AppRoles.Staff)]
    public async Task<ActionResult<ApiResponse<Guid>>> Create([FromBody] CreateSchoolRequest request)
    {
        var error = ValidarUbicacion(request, out var departamento);
        if (error != null) return BadRequest(ApiResponse<Guid>.Fail("VALIDATION", error));
        var school = new Core.Entities.School
        {
            Id = Guid.NewGuid(),
            Name = request.Name.Trim(),
            Address = Limpio(request.Address),
            Departamento = departamento,
            Provincia = Limpio(request.Provincia),
            Distrito = Limpio(request.Distrito),
            CreatedAt = DateTime.UtcNow
        };

        var id = await _schoolRepo.Create(school);
        return Ok(ApiResponse<Guid>.Ok(id));
    }

    [HttpPut("{id}")]
    [Authorize(Roles = AppRoles.Staff)]
    public async Task<IActionResult> UpdateSchool(Guid id, [FromBody] CreateSchoolRequest request)
    {
        var error = ValidarUbicacion(request, out var departamento);
        if (error != null) return BadRequest(ApiResponse<bool>.Fail("VALIDATION", error));
        using var conn = HttpContext.RequestServices.GetRequiredService<IDbConnectionFactory>().CreateConnection();
        await Dapper.SqlMapper.ExecuteAsync(conn, @"
            UPDATE schools SET name = @Name, address = @Address, departamento = @Departamento, provincia = @Provincia, distrito = @Distrito
            WHERE id = @Id",
            new { Id = id, Name = request.Name.Trim(), Address = Limpio(request.Address), Departamento = departamento,
                  Provincia = Limpio(request.Provincia), Distrito = Limpio(request.Distrito) });
        return Ok(ApiResponse<bool>.Ok(true));
    }

    [HttpDelete("{id}")]
    [Authorize(Roles = AppRoles.Staff)]
    public async Task<IActionResult> DeleteSchool(Guid id)
    {
        using var conn = HttpContext.RequestServices.GetRequiredService<IDbConnectionFactory>().CreateConnection();
        await Dapper.SqlMapper.ExecuteAsync(conn, "DELETE FROM grades WHERE school_id = @Id; DELETE FROM schools WHERE id = @Id", new { Id = id });
        return Ok(ApiResponse<bool>.Ok(true));
    }

    [HttpDelete("{id}/grades/{gradeId}")]
    [Authorize(Roles = AppRoles.Staff)]
    public async Task<IActionResult> DeleteGrade(Guid id, Guid gradeId)
    {
        using var conn = HttpContext.RequestServices.GetRequiredService<IDbConnectionFactory>().CreateConnection();
        await Dapper.SqlMapper.ExecuteAsync(conn, "DELETE FROM grades WHERE id = @Id", new { Id = gradeId });
        return Ok(ApiResponse<bool>.Ok(true));
    }

    [HttpGet("{id}/sections")]
    public async Task<ActionResult<ApiResponse<IEnumerable<SectionResponse>>>> GetSchoolSections(Guid id)
    {
        var sections = await _sectionRepo.GetBySchoolId(id);
        return Ok(ApiResponse<IEnumerable<SectionResponse>>.Ok(sections.Select(s => new SectionResponse
        {
            Id = s.Id,
            GroupName = s.GroupName,
            Name = s.Name,
            SortOrder = s.SortOrder
        })));
    }

    [HttpPut("{id}/sections")]
    [Authorize(Roles = AppRoles.Staff)]
    public async Task<IActionResult> SetSchoolSections(Guid id, [FromBody] SetSchoolSectionsRequest request)
    {
        await _sectionRepo.SetSchoolSections(id, request.SectionIds);
        return Ok(ApiResponse<bool>.Ok(true));
    }

    /// <summary>
    /// Crea (o reutiliza) un grado del colegio. Abierto a cualquier sesión porque el cliente,
    /// al subir su lista, elige su grado; si ya existe ese grado y año, devuelve el mismo id.
    /// </summary>
    [HttpPost("{id}/grades")]
    [Authorize]
    public async Task<ActionResult<ApiResponse<Guid>>> CreateGrade(Guid id, [FromBody] CreateGradeRequest request)
    {
        var nombre = (request.Name ?? "").Trim();
        if (nombre.Length < 2 || nombre.Length > 100)
            return BadRequest(ApiResponse<Guid>.Fail("VALIDATION", "El nombre del grado debe tener entre 2 y 100 caracteres"));
        var anioActual = DateTime.UtcNow.Year;
        if (request.Year < anioActual - 1 || request.Year > anioActual + 2)
            return BadRequest(ApiResponse<Guid>.Fail("VALIDATION", "El año escolar no es válido"));

        using var conn = HttpContext.RequestServices.GetRequiredService<IDbConnectionFactory>().CreateConnection();
        var colegio = await Dapper.SqlMapper.ExecuteScalarAsync<int>(conn, "SELECT COUNT(*) FROM schools WHERE id = @Id", new { Id = id });
        if (colegio == 0) return NotFound(ApiResponse<Guid>.Fail("NOT_FOUND", "Colegio no encontrado"));
        var existente = await Dapper.SqlMapper.ExecuteScalarAsync<Guid?>(conn,
            "SELECT id FROM grades WHERE school_id = @Id AND lower(name) = lower(@Nombre) AND year = @Year LIMIT 1",
            new { Id = id, Nombre = nombre, request.Year });
        if (existente.HasValue) return Ok(ApiResponse<Guid>.Ok(existente.Value));

        var grade = new Core.Entities.Grade
        {
            Id = Guid.NewGuid(),
            SchoolId = id,
            Name = nombre,
            Year = request.Year,
            CreatedAt = DateTime.UtcNow
        };

        var gradeId = await _gradeRepo.Create(grade);
        return Ok(ApiResponse<Guid>.Ok(gradeId));
    }
}

[ApiController]
[Route("api/sections")]
public class SectionsController : ControllerBase
{
    private readonly SectionRepository _sectionRepo;

    public SectionsController(SectionRepository sectionRepo)
    {
        _sectionRepo = sectionRepo;
    }

    [HttpGet]
    public async Task<ActionResult<ApiResponse<IEnumerable<SectionResponse>>>> GetAll()
    {
        var sections = await _sectionRepo.GetAll();
        return Ok(ApiResponse<IEnumerable<SectionResponse>>.Ok(sections.Select(s => new SectionResponse
        {
            Id = s.Id,
            GroupName = s.GroupName,
            Name = s.Name,
            SortOrder = s.SortOrder
        })));
    }

    [HttpPost]
    [Authorize(Roles = AppRoles.Staff)]
    public async Task<ActionResult<ApiResponse<Guid>>> Create([FromBody] CreateSectionRequest request)
    {
        var section = new Core.Entities.Section
        {
            Id = Guid.NewGuid(),
            GroupName = request.GroupName,
            Name = request.Name,
            SortOrder = request.SortOrder,
            CreatedAt = DateTime.UtcNow
        };
        var id = await _sectionRepo.Create(section);
        return Ok(ApiResponse<Guid>.Ok(id));
    }

    [HttpPut("{id}")]
    [Authorize(Roles = AppRoles.Staff)]
    public async Task<IActionResult> Update(Guid id, [FromBody] CreateSectionRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Name))
            return BadRequest(ApiResponse<bool>.Fail("INVALID_NAME", "Escribe el nombre de la sección"));
        var ok = await _sectionRepo.Update(id, request.GroupName?.Trim() ?? "", request.Name.Trim(), request.SortOrder);
        if (!ok) return NotFound(ApiResponse<bool>.Fail("NOT_FOUND", "Sección no encontrada"));
        return Ok(ApiResponse<bool>.Ok(true));
    }

    [HttpDelete("{id}")]
    [Authorize(Roles = AppRoles.Staff)]
    public async Task<IActionResult> Delete(Guid id)
    {
        await _sectionRepo.Delete(id);
        return Ok(ApiResponse<bool>.Ok(true));
    }
}

public class SectionResponse
{
    public Guid Id { get; set; }
    public string GroupName { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public int SortOrder { get; set; }
}

public class CreateSectionRequest
{
    public string GroupName { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public int SortOrder { get; set; }
}

[ApiController]
[Route("api/categories")]
public class CategoriesController : ControllerBase
{
    private readonly CategoryRepository _catRepo;
    public CategoriesController(CategoryRepository catRepo) { _catRepo = catRepo; }

    [HttpGet]
    public async Task<IActionResult> GetAll()
    {
        var cats = await _catRepo.GetAll();
        return Ok(ApiResponse<IEnumerable<object>>.Ok(cats.Select(c => new { c.Id, c.Name })));
    }

    [HttpPost]
    [Authorize(Roles = AppRoles.Staff)]
    public async Task<IActionResult> Create([FromBody] CreateCategoryRequest req)
    {
        if (string.IsNullOrWhiteSpace(req.Name))
            return BadRequest(ApiResponse<Guid>.Fail("INVALID_NAME", "Escribe el nombre de la categoría"));
        var id = await _catRepo.Create(req.Name.Trim());
        return Ok(ApiResponse<Guid>.Ok(id));
    }

    /// <summary>Renombra la categoría y, en la misma transacción, los productos que la usan.</summary>
    [HttpPut("{id}")]
    [Authorize(Roles = AppRoles.Staff)]
    public async Task<IActionResult> Update(Guid id, [FromBody] CreateCategoryRequest req)
    {
        if (string.IsNullOrWhiteSpace(req.Name))
            return BadRequest(ApiResponse<bool>.Fail("INVALID_NAME", "Escribe el nombre de la categoría"));
        var resultado = await _catRepo.Rename(id, req.Name.Trim());
        return resultado switch
        {
            CategoryRepository.RenameResult.NotFound => NotFound(ApiResponse<bool>.Fail("NOT_FOUND", "Categoría no encontrada")),
            CategoryRepository.RenameResult.Duplicate => Conflict(ApiResponse<bool>.Fail("CATEGORY_EXISTS", "Ya existe una categoría con ese nombre")),
            _ => Ok(ApiResponse<bool>.Ok(true))
        };
    }

    [HttpDelete("{id}")]
    [Authorize(Roles = AppRoles.Staff)]
    public async Task<IActionResult> Delete(Guid id)
    {
        var enUso = await _catRepo.ProductsUsing(id);
        if (enUso > 0)
            return Conflict(ApiResponse<bool>.Fail("CATEGORY_IN_USE",
                $"No se puede borrar: {enUso} producto(s) usan esta categoría. Cámbialos de categoría primero."));
        await _catRepo.Delete(id);
        return Ok(ApiResponse<bool>.Ok(true));
    }
}

public class CreateCategoryRequest { public string Name { get; set; } = ""; }

public class SetSchoolSectionsRequest
{
    public List<Guid> SectionIds { get; set; } = new();
}

public class CreateSchoolRequest
{
    public string Name { get; set; } = string.Empty;
    public string? Address { get; set; }
    /// <summary>null = Arequipa</summary>
    public string? Departamento { get; set; }
    public string? Provincia { get; set; }
    public string? Distrito { get; set; }
}

public class CreateGradeRequest
{
    public string Name { get; set; } = string.Empty;
    public int Year { get; set; }
}