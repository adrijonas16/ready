using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using UtilesApi.DTOs;
using UtilesApi.Services;
using UtilesApi.Infrastructure.Database;
using System.Security.Claims;
using Microsoft.IdentityModel.Tokens;
using System.IdentityModel.Tokens.Jwt;
using System.Text;

namespace UtilesApi.Controllers;

[ApiController]
[Route("api/[controller]")]
public class AuthController : ControllerBase
{
    private readonly IAuthService _authService;
    private readonly UserRepository _userRepo;

    public AuthController(IAuthService authService, UserRepository userRepo)
    {
        _authService = authService;
        _userRepo = userRepo;
    }

    [HttpPost("register")]
    [AllowAnonymous]
    public async Task<ActionResult<ApiResponse<AuthResponse>>> Register([FromBody] UserRegisterRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Email) || !request.Email.Contains('@'))
            return BadRequest(ApiResponse<AuthResponse>.Fail("INVALID_EMAIL", "Escribe un correo válido"));
        if (string.IsNullOrEmpty(request.Password) || request.Password.Length < 6)
            return BadRequest(ApiResponse<AuthResponse>.Fail("WEAK_PASSWORD", "La contraseña debe tener al menos 6 caracteres"));
        if (string.IsNullOrWhiteSpace(request.Name))
            return BadRequest(ApiResponse<AuthResponse>.Fail("INVALID_NAME", "Escribe tu nombre"));

        var user = await _authService.Register(request.Email, request.Password, request.Name.Trim());
        if (user == null)
            return BadRequest(ApiResponse<AuthResponse>.Fail("EMAIL_EXISTS", "El email ya esta registrado"));

        var token = _authService.GenerateToken(user);
        return Ok(ApiResponse<AuthResponse>.Ok(new AuthResponse { Token = token, User = UserResponse.From(user) }));
    }

    [HttpPost("login")]
    [AllowAnonymous]
    public async Task<ActionResult<ApiResponse<AuthResponse>>> Login([FromBody] UserLoginRequest request)
    {
        var (user, token) = await _authService.Login(request.Email, request.Password);
        if (user == null)
            return Unauthorized(ApiResponse<AuthResponse>.Fail("INVALID_CREDENTIALS", "Credenciales invalidas"));

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
            if (string.IsNullOrWhiteSpace(request.Name))
                return BadRequest(ApiResponse<UserResponse>.Fail("INVALID_NAME", "Escribe tu nombre"));
            user.Name = request.Name.Trim();
        }

        var phone = Limpio(request.Phone);
        if (phone != null && !Validacion.TelefonoValido(phone))
            return BadRequest(ApiResponse<UserResponse>.Fail("INVALID_PHONE", "El teléfono debe tener al menos 6 dígitos"));
        var documento = Limpio(request.Documento);
        if (documento != null && !Validacion.DocumentoValido(documento))
            return BadRequest(ApiResponse<UserResponse>.Fail("INVALID_DOCUMENT", "El DNI debe tener 8 dígitos o el RUC 11"));

        user.Phone = phone;
        user.Address = Limpio(request.Address);
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

    [HttpGet]
    public async Task<ActionResult<ApiResponse<IEnumerable<SchoolResponse>>>> GetAll()
    {
        var schools = await _schoolRepo.GetAll();
        return Ok(ApiResponse<IEnumerable<SchoolResponse>>.Ok(schools.Select(s => new SchoolResponse
        {
            Id = s.Id,
            Name = s.Name,
            Address = s.Address
        })));
    }

    [HttpGet("{id}")]
    public async Task<ActionResult<ApiResponse<SchoolResponse>>> GetById(Guid id)
    {
        var school = await _schoolRepo.GetById(id);
        if (school == null)
            return NotFound(ApiResponse<SchoolResponse>.Fail("NOT_FOUND", "Colegio no encontrado"));

        return Ok(ApiResponse<SchoolResponse>.Ok(new SchoolResponse
        {
            Id = school.Id,
            Name = school.Name,
            Address = school.Address
        }));
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
        var school = new Core.Entities.School
        {
            Id = Guid.NewGuid(),
            Name = request.Name,
            Address = request.Address,
            CreatedAt = DateTime.UtcNow
        };

        var id = await _schoolRepo.Create(school);
        return Ok(ApiResponse<Guid>.Ok(id));
    }

    [HttpPut("{id}")]
    [Authorize(Roles = AppRoles.Staff)]
    public async Task<IActionResult> UpdateSchool(Guid id, [FromBody] CreateSchoolRequest request)
    {
        using var conn = HttpContext.RequestServices.GetRequiredService<IDbConnectionFactory>().CreateConnection();
        await Dapper.SqlMapper.ExecuteAsync(conn, "UPDATE schools SET name = @Name, address = @Address WHERE id = @Id",
            new { Id = id, request.Name, request.Address });
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

    [HttpPost("{id}/grades")]
    [Authorize(Roles = AppRoles.Staff)]
    public async Task<ActionResult<ApiResponse<Guid>>> CreateGrade(Guid id, [FromBody] CreateGradeRequest request)
    {
        var grade = new Core.Entities.Grade
        {
            Id = Guid.NewGuid(),
            SchoolId = id,
            Name = request.Name,
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
}

public class CreateGradeRequest
{
    public string Name { get; set; } = string.Empty;
    public int Year { get; set; }
}