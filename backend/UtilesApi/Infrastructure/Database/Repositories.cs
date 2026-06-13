using System.Data;
using Dapper;
using UtilesApi.Core.Entities;

namespace UtilesApi.Infrastructure.Database;

public class UserRepository
{
    private readonly IDbConnectionFactory _db;

    public UserRepository(IDbConnectionFactory db)
    {
        _db = db;
    }

    public async Task<User?> GetById(Guid id)
    {
        using var connection = _db.CreateConnection();
        var query = @"SELECT id AS Id, email AS Email, password_hash AS PasswordHash, name AS Name, 
                      phone AS Phone, address AS Address, role AS Role, created_at AS CreatedAt, updated_at AS UpdatedAt 
                      FROM users WHERE id = @Id";
        var result = await connection.QueryFirstOrDefaultAsync<User>(query, new { Id = id });
        if (result != null)
        {
            Console.WriteLine($"[DEBUG] User from DB - Email: {result.Email}, PwdHash: '{result.PasswordHash}'");
        }
        return result;
    }

    public async Task<User?> GetByEmail(string email)
    {
        using var connection = _db.CreateConnection();
        var query = @"SELECT id AS Id, email AS Email, password_hash AS PasswordHash, name AS Name, 
                      phone AS Phone, address AS Address, role AS Role, created_at AS CreatedAt, updated_at AS UpdatedAt 
                      FROM users WHERE email = @Email";
        var result = await connection.QueryFirstOrDefaultAsync<User>(query, new { Email = email });
        if (result != null)
        {
            Console.WriteLine($"[DEBUG] User from DB - Email: {result.Email}, PwdHash: '{result.PasswordHash}'");
        }
        return result;
    }

    public async Task<Guid> Create(User user)
    {
        using var connection = _db.CreateConnection();
        Console.WriteLine($"[DEBUG] Creating user with password_hash: '{user.PasswordHash}'");
        var id = await connection.ExecuteScalarAsync<Guid>(@"
            INSERT INTO users (email, password_hash, name, phone, address, role)
            VALUES (@Email, @PasswordHash, @Name, @Phone, @Address, @Role)
            RETURNING id", user);
        Console.WriteLine($"[DEBUG] User created with id: {id}");
        return id;
    }

    public async Task Update(User user)
    {
        using var connection = _db.CreateConnection();
        await connection.ExecuteAsync(@"
            UPDATE users SET name = @Name, phone = @Phone, address = @Address, updated_at = NOW()
            WHERE id = @Id", user);
    }
}

public class SchoolRepository
{
    private readonly IDbConnectionFactory _db;

    public SchoolRepository(IDbConnectionFactory db)
    {
        _db = db;
    }

    public async Task<IEnumerable<School>> GetAll()
    {
        using var connection = _db.CreateConnection();
        return await connection.QueryAsync<School>("SELECT * FROM schools ORDER BY name");
    }

    public async Task<School?> GetById(Guid id)
    {
        using var connection = _db.CreateConnection();
        return await connection.QueryFirstOrDefaultAsync<School>(@"
            SELECT 
                id as Id,
                name as Name,
                address as Address,
                created_at as CreatedAt
            FROM schools WHERE id = @Id", new { Id = id });
    }

    public async Task<Guid> Create(School school)
    {
        using var connection = _db.CreateConnection();
        var id = await connection.ExecuteScalarAsync<Guid>(@"
            INSERT INTO schools (name, address) VALUES (@Name, @Address) RETURNING id", school);
        return id;
    }
}

public class GradeRepository
{
    private readonly IDbConnectionFactory _db;

    public GradeRepository(IDbConnectionFactory db)
    {
        _db = db;
    }

    public async Task<IEnumerable<Grade>> GetBySchoolId(Guid schoolId)
    {
        using var connection = _db.CreateConnection();
        return await connection.QueryAsync<Grade>(
            "SELECT * FROM grades WHERE school_id = @SchoolId ORDER BY year, name",
            new { SchoolId = schoolId });
    }

    public async Task<Grade?> GetById(Guid id)
    {
        using var connection = _db.CreateConnection();
        return await connection.QueryFirstOrDefaultAsync<Grade>(@"
            SELECT 
                id as Id,
                school_id as SchoolId,
                name as Name,
                year as Year,
                created_at as CreatedAt
            FROM grades WHERE id = @Id", new { Id = id });
    }

    public async Task<Guid> Create(Grade grade)
    {
        using var connection = _db.CreateConnection();
        var id = await connection.ExecuteScalarAsync<Guid>(@"
            INSERT INTO grades (school_id, name, year) VALUES (@SchoolId, @Name, @Year) RETURNING id", grade);
        return id;
    }
}

public class CategoryRepository
{
    private readonly IDbConnectionFactory _db;
    public CategoryRepository(IDbConnectionFactory db) { _db = db; }

    public async Task<IEnumerable<Category>> GetAll()
    {
        using var connection = _db.CreateConnection();
        return await connection.QueryAsync<Category>("SELECT id as Id, name as Name, created_at as CreatedAt FROM categories ORDER BY name");
    }

    public async Task<Guid> Create(string name)
    {
        using var connection = _db.CreateConnection();
        return await connection.ExecuteScalarAsync<Guid>("INSERT INTO categories (name) VALUES (@Name) ON CONFLICT (name) DO UPDATE SET name = @Name RETURNING id", new { Name = name });
    }

    public async Task Delete(Guid id)
    {
        using var connection = _db.CreateConnection();
        await connection.ExecuteAsync("DELETE FROM categories WHERE id = @Id", new { Id = id });
    }
}

public class SectionRepository
{
    private readonly IDbConnectionFactory _db;

    public SectionRepository(IDbConnectionFactory db)
    {
        _db = db;
    }

    public async Task<IEnumerable<Section>> GetAll()
    {
        using var connection = _db.CreateConnection();
        return await connection.QueryAsync<Section>(
            "SELECT id as Id, group_name as GroupName, name as Name, sort_order as SortOrder, created_at as CreatedAt FROM sections ORDER BY sort_order");
    }

    public async Task<Guid> Create(Section section)
    {
        using var connection = _db.CreateConnection();
        var id = await connection.ExecuteScalarAsync<Guid>(@"
            INSERT INTO sections (group_name, name, sort_order) VALUES (@GroupName, @Name, @SortOrder) RETURNING id", section);
        return id;
    }

    public async Task Delete(Guid id)
    {
        using var connection = _db.CreateConnection();
        await connection.ExecuteAsync("DELETE FROM sections WHERE id = @Id", new { Id = id });
    }

    public async Task<IEnumerable<Section>> GetBySchoolId(Guid schoolId)
    {
        using var connection = _db.CreateConnection();
        return await connection.QueryAsync<Section>(@"
            SELECT s.id as Id, s.group_name as GroupName, s.name as Name, s.sort_order as SortOrder, s.created_at as CreatedAt
            FROM sections s
            INNER JOIN school_sections ss ON ss.section_id = s.id
            WHERE ss.school_id = @SchoolId
            ORDER BY s.sort_order", new { SchoolId = schoolId });
    }

    public async Task SetSchoolSections(Guid schoolId, IEnumerable<Guid> sectionIds)
    {
        using var connection = _db.CreateConnection();
        await connection.ExecuteAsync("DELETE FROM school_sections WHERE school_id = @SchoolId", new { SchoolId = schoolId });
        foreach (var sectionId in sectionIds)
        {
            await connection.ExecuteAsync(
                "INSERT INTO school_sections (school_id, section_id) VALUES (@SchoolId, @SectionId) ON CONFLICT DO NOTHING",
                new { SchoolId = schoolId, SectionId = sectionId });
        }
    }
}