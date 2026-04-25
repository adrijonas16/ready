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
    public async Task<ActionResult<ApiResponse<AuthResponse>>> Register([FromBody] UserRegisterRequest request)
    {
        var user = await _authService.Register(request.Email, request.Password, request.Name);
        if (user == null)
            return BadRequest(ApiResponse<AuthResponse>.Fail("EMAIL_EXISTS", "El email ya esta registrado"));

        var token = _authService.GenerateToken(user);
        
        return Ok(ApiResponse<AuthResponse>.Ok(new AuthResponse
        {
            Token = token,
            User = new UserResponse
            {
                Id = user.Id,
                Email = user.Email,
                Name = user.Name,
                Role = user.Role.ToString()
            }
        }));
    }

    [HttpPost("login")]
    public async Task<ActionResult<ApiResponse<AuthResponse>>> Login([FromBody] UserLoginRequest request)
    {
        var (user, token) = await _authService.Login(request.Email, request.Password);
        if (user == null)
            return Unauthorized(ApiResponse<AuthResponse>.Fail("INVALID_CREDENTIALS", "Credenciales invalidas"));

        return Ok(ApiResponse<AuthResponse>.Ok(new AuthResponse
        {
            Token = token,
            User = new UserResponse
            {
                Id = user.Id,
                Email = user.Email,
                Name = user.Name,
                Phone = user.Phone,
                Address = user.Address,
                Role = user.Role.ToString()
            }
        }));
    }

    [HttpGet("me")]
    public async Task<ActionResult<ApiResponse<UserResponse>>> GetCurrentUser()
    {
        var userId = GetUserIdFromToken();
        if (userId == null)
            return Unauthorized(ApiResponse<UserResponse>.Fail("UNAUTHORIZED", "No autorizado"));

        var user = await _userRepo.GetById(userId.Value);
        if (user == null)
            return NotFound(ApiResponse<UserResponse>.Fail("USER_NOT_FOUND", "Usuario no encontrado"));

        return Ok(ApiResponse<UserResponse>.Ok(new UserResponse
        {
            Id = user.Id,
            Email = user.Email,
            Name = user.Name,
            Phone = user.Phone,
            Address = user.Address,
            Role = user.Role.ToString()
        }));
    }

    private Guid? GetUserIdFromToken()
    {
        var claim = User.FindFirst(ClaimTypes.NameIdentifier);
        if (claim == null || !Guid.TryParse(claim.Value, out var userId))
            return null;
        return userId;
    }
}

[ApiController]
[Route("api/schools")]
public class SchoolsController : ControllerBase
{
    private readonly SchoolRepository _schoolRepo;
    private readonly GradeRepository _gradeRepo;

    public SchoolsController(SchoolRepository schoolRepo, GradeRepository gradeRepo)
    {
        _schoolRepo = schoolRepo;
        _gradeRepo = gradeRepo;
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

    [HttpPost("{id}/grades")]
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