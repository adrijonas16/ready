using UtilesApi.Core.Entities;

namespace UtilesApi.Services;

public interface IAuthService
{
    Task<User?> Register(string email, string password, string name);
    Task<User?> RegisterWithGoogle(string email, string name, string googleId);
    Task<(User?, string)> Login(string email, string password);
    string GenerateToken(User user);
}

public class AuthService : IAuthService
{
    private readonly Infrastructure.Database.UserRepository _userRepo;
    private readonly IConfiguration _configuration;

    public AuthService(Infrastructure.Database.UserRepository userRepo, IConfiguration configuration)
    {
        _userRepo = userRepo;
        _configuration = configuration;
    }

    public async Task<User?> Register(string email, string password, string name)
    {
        var existing = await _userRepo.GetByEmail(email);
        if (existing != null)
            return null;

        var user = new User
        {
            Id = Guid.NewGuid(),
            Email = email,
            PasswordHash = BCrypt.Net.BCrypt.HashPassword(password),
            Name = name,
            Role = UserRole.USER,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };

        await _userRepo.Create(user);
        return user;
    }

    public async Task<User?> RegisterWithGoogle(string email, string name, string googleId)
    {
        var existing = await _userRepo.GetByEmail(email);
        if (existing != null)
            return existing;

        var user = new User
        {
            Id = Guid.NewGuid(),
            Email = email,
            PasswordHash = BCrypt.Net.BCrypt.HashPassword(Guid.NewGuid().ToString()),
            Name = name,
            Role = UserRole.USER,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };

        await _userRepo.Create(user);
        return user;
    }

    public async Task<(User?, string)> Login(string email, string password)
    {
        var user = await _userRepo.GetByEmail(email);
        if (user == null)
            return (null, string.Empty);

        Console.WriteLine($"User found: {user.Email}, PwdHash length: {user.PasswordHash?.Length}, Hash starts with: {user.PasswordHash?.Substring(0, Math.Min(10, user.PasswordHash.Length))}");

        bool passwordValid = false;
        
        if (!string.IsNullOrEmpty(user.PasswordHash))
        {
            var pwdHash = user.PasswordHash;
            Console.WriteLine($"Hash full: {pwdHash}");
            Console.WriteLine($"Attempting to verify password: {password}");
            
            if (pwdHash.StartsWith("$2"))
            {
                try 
                {
                    passwordValid = BCrypt.Net.BCrypt.Verify(password, pwdHash);
                    Console.WriteLine($"BCrypt verification result: {passwordValid}");
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"BCrypt error: {ex.Message}");
                }
            }
        }
        
        Console.WriteLine($"Final passwordValid: {passwordValid}");
        
        if (!passwordValid)
            return (null, string.Empty);

        return (user, GenerateToken(user));
    }

    public string GenerateToken(User user)
    {
        var key = _configuration["Jwt:Key"] ?? "DefaultSecretKey123456789012345678901234";
        var issuer = _configuration["Jwt:Issuer"] ?? "UtilesApi";
        var audience = _configuration["Jwt:Audience"] ?? "UtilesClient";

        var claims = new[]
        {
            new System.Security.Claims.Claim(System.Security.Claims.ClaimTypes.NameIdentifier, user.Id.ToString()),
            new System.Security.Claims.Claim(System.Security.Claims.ClaimTypes.Email, user.Email),
            new System.Security.Claims.Claim(System.Security.Claims.ClaimTypes.Role, user.Role.ToString())
        };

        var securityKey = new Microsoft.IdentityModel.Tokens.SymmetricSecurityKey(
            System.Text.Encoding.UTF8.GetBytes(key));
        var credentials = new Microsoft.IdentityModel.Tokens.SigningCredentials(
            securityKey, Microsoft.IdentityModel.Tokens.SecurityAlgorithms.HmacSha256);

        var token = new System.IdentityModel.Tokens.Jwt.JwtSecurityToken(
            issuer, audience, claims,
            expires: DateTime.UtcNow.AddDays(7),
            signingCredentials: credentials);

        return new System.IdentityModel.Tokens.Jwt.JwtSecurityTokenHandler().WriteToken(token);
    }
}