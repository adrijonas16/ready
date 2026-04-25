using System.Data;
using Dapper;
using UtilesApi.Core.Entities;

namespace UtilesApi.Infrastructure.Database;

public class SupplyListRepository
{
    private readonly IDbConnectionFactory _db;

    public SupplyListRepository(IDbConnectionFactory db)
    {
        _db = db;
    }

    public async Task<SupplyList?> GetById(Guid id)
    {
        using var connection = _db.CreateConnection();
        return await connection.QueryFirstOrDefaultAsync<SupplyList>(@"
            SELECT 
                id as Id,
                user_id as UserId,
                school_id as SchoolId,
                grade_id as GradeId,
                year as Year,
                image_url as ImageUrl,
                ocr_text as OcrText,
                parsed_college as ParsedCollege,
                parsed_grade as ParsedGrade,
                estado as Estado,
                es_oficial as EsOficial,
                observaciones as Observaciones,
                submitted_by as SubmittedBy,
                fecha_subida as FechaSubida,
                fecha_inicio_revision as FechaInicioRevision,
                fecha_validacion as FechaValidacion,
                created_at as CreatedAt,
                updated_at as UpdatedAt
            FROM supply_lists WHERE id = @Id", new { Id = id });
    }

    public async Task<IEnumerable<SupplyList>> GetAll(string? estado = null, bool? esOficial = null, Guid? userId = null)
    {
        using var connection = _db.CreateConnection();
        var sql = "SELECT * FROM supply_lists WHERE 1=1";
        var parameters = new DynamicParameters();

        if (!string.IsNullOrEmpty(estado))
        {
            sql += " AND estado = @Estado";
            parameters.Add("Estado", estado);
        }
        if (esOficial.HasValue)
        {
            sql += " AND es_oficial = @EsOficial";
            parameters.Add("EsOficial", esOficial.Value);
        }
        if (userId.HasValue)
        {
            sql += " AND user_id = @UserId";
            parameters.Add("UserId", userId.Value);
        }

        sql += " ORDER BY created_at DESC";
        return await connection.QueryAsync<SupplyList>(sql, parameters);
    }

    public async Task<IEnumerable<SupplyList>> GetOfficialLists(Guid? schoolId = null, Guid? gradeId = null)
    {
        using var connection = _db.CreateConnection();
        var sql = "SELECT * FROM supply_lists WHERE es_oficial = true";
        var parameters = new DynamicParameters();

        if (schoolId.HasValue)
        {
            sql += " AND school_id = @SchoolId";
            parameters.Add("SchoolId", schoolId.Value);
        }
        if (gradeId.HasValue)
        {
            sql += " AND grade_id = @GradeId";
            parameters.Add("GradeId", gradeId.Value);
        }

        sql += " ORDER BY year DESC, created_at DESC";
        return await connection.QueryAsync<SupplyList>(sql, parameters);
    }

    public async Task<Guid> Create(SupplyList list)
    {
        using var connection = _db.CreateConnection();
        var id = await connection.ExecuteScalarAsync<Guid>(@"
            INSERT INTO supply_lists (user_id, school_id, grade_id, year, image_url, estado, es_oficial, submitted_by, fecha_subida)
            VALUES (@UserId, @SchoolId, @GradeId, @Year, @ImageUrl, @Estado, @EsOficial, @SubmittedBy, @FechaSubida)
            RETURNING id", new {
                list.UserId,
                list.SchoolId,
                list.GradeId,
                list.Year,
                list.ImageUrl,
                Estado = list.Estado.ToString(),
                list.EsOficial,
                list.SubmittedBy,
                list.FechaSubida
            });
        return id;
    }

    public async Task Update(SupplyList list)
    {
        using var connection = _db.CreateConnection();
        await connection.ExecuteAsync(@"
            UPDATE supply_lists SET
                school_id = @SchoolId,
                grade_id = @GradeId,
                year = @Year,
                image_url = @ImageUrl,
                ocr_text = @OcrText,
                parsed_college = @ParsedCollege,
                parsed_grade = @ParsedGrade,
                estado = @Estado,
                observaciones = @Observaciones,
                fecha_inicio_revision = @FechaInicioRevision,
                fecha_validacion = @FechaValidacion,
                updated_at = NOW()
            WHERE id = @Id", new {
                list.Id,
                list.SchoolId,
                list.GradeId,
                list.Year,
                list.ImageUrl,
                list.OcrText,
                list.ParsedCollege,
                list.ParsedGrade,
                Estado = list.Estado.ToString(),
                list.Observaciones,
                list.FechaInicioRevision,
                list.FechaValidacion
            });
    }

    public async Task UpdateStatus(Guid id, ListStatus status, string? observaciones = null)
    {
        using var connection = _db.CreateConnection();
        var sql = "UPDATE supply_lists SET estado = @Status, updated_at = NOW()";
        var parameters = new DynamicParameters();
        parameters.Add("Id", id);
        parameters.Add("Status", status.ToString());

        if (status == ListStatus.EN_REVISION)
        {
            sql += ", fecha_inicio_revision = NOW()";
        }
        if (status == ListStatus.VALIDADA)
        {
            sql += ", fecha_validacion = NOW()";
        }
        if (!string.IsNullOrEmpty(observaciones))
        {
            sql += ", observaciones = @Observaciones";
            parameters.Add("Observaciones", observaciones);
        }

        sql += " WHERE id = @Id";
        await connection.ExecuteAsync(sql, parameters);
    }
}

public class SupplyItemRepository
{
    private readonly IDbConnectionFactory _db;

    public SupplyItemRepository(IDbConnectionFactory db)
    {
        _db = db;
    }

    public async Task<IEnumerable<SupplyItem>> GetByListId(Guid listId)
    {
        using var connection = _db.CreateConnection();
        return await connection.QueryAsync<SupplyItem>(@"
            SELECT 
                id as Id,
                supply_list_id as SupplyListId,
                product_id as ProductId,
                nombre_original as NombreOriginal,
                nombre_detectado as NombreDetectado,
                cantidad as Cantidad,
                notas as Notas,
                matched_product_id as MatchedProductId,
                matched_quantity as MatchedQuantity,
                price_at_match as PriceAtMatch,
                user_custom_quantity as UserCustomQuantity,
                user_notas as UserNotas,
                created_at as CreatedAt,
                updated_at as UpdatedAt
            FROM supply_items WHERE supply_list_id = @ListId",
            new { ListId = listId });
    }

    public async Task<Guid> Create(SupplyItem item)
    {
        using var connection = _db.CreateConnection();
        var id = await connection.ExecuteScalarAsync<Guid>(@"
            INSERT INTO supply_items (supply_list_id, product_id, nombre_original, nombre_detectado, cantidad, notas, matched_product_id, matched_quantity, price_at_match)
            VALUES (@SupplyListId, @ProductId, @NombreOriginal, @NombreDetectado, @Cantidad, @Notas, @MatchedProductId, @MatchedQuantity, @PriceAtMatch)
            RETURNING id", new {
                item.SupplyListId,
                item.ProductId,
                item.NombreOriginal,
                item.NombreDetectado,
                item.Cantidad,
                item.Notas,
                item.MatchedProductId,
                item.MatchedQuantity,
                item.PriceAtMatch
            });
        return id;
    }

    public async Task Update(SupplyItem item)
    {
        using var connection = _db.CreateConnection();
        await connection.ExecuteAsync(@"
            UPDATE supply_items SET
                product_id = @ProductId,
                nombre_detectado = @NombreDetectado,
                cantidad = @Cantidad,
                notas = @Notas,
                matched_product_id = @MatchedProductId,
                matched_quantity = @MatchedQuantity,
                price_at_match = @PriceAtMatch,
                user_custom_quantity = @UserCustomQuantity,
                user_notas = @UserNotas,
                updated_at = NOW()
            WHERE id = @Id", item);
    }

    public async Task Delete(Guid id)
    {
        using var connection = _db.CreateConnection();
        await connection.ExecuteAsync("DELETE FROM supply_items WHERE id = @Id", new { Id = id });
    }
}