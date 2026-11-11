using System.Data;
using Dapper;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using UtilesApi.DTOs;
using UtilesApi.Infrastructure.Database;
using UtilesApi.Services;

namespace UtilesApi.Controllers;

/// <summary>Configuración de la tienda (envío, contacto, horario). Una sola fila JSON en store_settings.</summary>
[ApiController]
[Route("api/settings")]
public class SettingsController : ControllerBase
{
    private readonly IDbConnectionFactory _db;

    public SettingsController(IDbConnectionFactory db) { _db = db; }

    [HttpGet]
    [AllowAnonymous]
    public async Task<ActionResult<ApiResponse<StoreSettingsDto>>> Get()
    {
        using var conn = _db.CreateConnection();
        return Ok(ApiResponse<StoreSettingsDto>.Ok(await StoreSettings.Leer(conn)));
    }

    [HttpPut]
    [Authorize(Roles = AppRoles.Staff)]
    public async Task<ActionResult<ApiResponse<StoreSettingsDto>>> Update([FromBody] StoreSettingsDto request)
    {
        if (request.CostoEnvio < 0)
            return BadRequest(ApiResponse<StoreSettingsDto>.Fail("INVALID_SETTINGS", "El costo de envío no puede ser negativo"));
        if (request.EnvioGratisDesde is < 0)
            return BadRequest(ApiResponse<StoreSettingsDto>.Fail("INVALID_SETTINGS", "El monto para envío gratis no puede ser negativo"));
        if (request.DiasEntrega < 0 || request.DiasEntrega > 60)
            return BadRequest(ApiResponse<StoreSettingsDto>.Fail("INVALID_SETTINGS", "Los días de entrega deben estar entre 0 y 60"));
        if (request.CostoEnvio > 1000)
            return BadRequest(ApiResponse<StoreSettingsDto>.Fail("INVALID_SETTINGS", "El costo de envío debe estar entre 0 y 1000"));
        if (request.EnvioGratisDesde is > 100000)
            return BadRequest(ApiResponse<StoreSettingsDto>.Fail("INVALID_SETTINGS", "El monto para envío gratis debe estar entre 0 y 100000"));
        var whatsapp = new string((request.Whatsapp ?? "").Where(char.IsAsciiDigit).ToArray());
        if (!string.IsNullOrWhiteSpace(request.Whatsapp) && (whatsapp.Length < 9 || whatsapp.Length > 15))
            return BadRequest(ApiResponse<StoreSettingsDto>.Fail("INVALID_SETTINGS", "El WhatsApp debe tener entre 9 y 15 dígitos (con código de país, ej. 51951237582)"));
        if (!Validacion.Largo(request.Telefono, 30))
            return BadRequest(ApiResponse<StoreSettingsDto>.Fail("INVALID_SETTINGS", "El teléfono puede tener hasta 30 caracteres"));
        if (!Validacion.Largo(request.HorarioEntrega, 200))
            return BadRequest(ApiResponse<StoreSettingsDto>.Fail("INVALID_SETTINGS", "El horario puede tener hasta 200 caracteres"));
        if (!Validacion.Largo(request.Zonas, 2000))
            return BadRequest(ApiResponse<StoreSettingsDto>.Fail("INVALID_SETTINGS", "Las zonas pueden tener hasta 2000 caracteres"));

        request.Whatsapp = whatsapp;
        request.Telefono = (request.Telefono ?? "").Trim();
        request.HorarioEntrega = (request.HorarioEntrega ?? "").Trim();
        request.Zonas = string.IsNullOrWhiteSpace(request.Zonas) ? null : request.Zonas.Trim();

        using var conn = _db.CreateConnection();
        await conn.ExecuteAsync(@"
            INSERT INTO store_settings (id, data, updated_at) VALUES (1, @Data::jsonb, NOW())
            ON CONFLICT (id) DO UPDATE SET data = EXCLUDED.data, updated_at = NOW()",
            new { Data = Validacion.ASerializar(request) });
        return Ok(ApiResponse<StoreSettingsDto>.Ok(request));
    }
}

public static class StoreSettings
{
    /// <summary>Valores de demostración (los mismos que siembra DatabaseInitializer).</summary>
    public static StoreSettingsDto PorDefecto() => new()
    {
        CostoEnvio = 8,
        EnvioGratisDesde = 150,
        Whatsapp = "51951237582",
        Telefono = "951 237 582",
        HorarioEntrega = "Lunes a sábado, 9:00 a 18:00",
        DiasEntrega = 2
    };

    public static async Task<StoreSettingsDto> Leer(IDbConnection conn, IDbTransaction? tx = null)
    {
        var json = await conn.ExecuteScalarAsync<string?>("SELECT data::text FROM store_settings WHERE id = 1", transaction: tx);
        return Validacion.Leer<StoreSettingsDto>(json) ?? PorDefecto();
    }

    /// <summary>Costo de envío para un subtotal: gratis si llega a envioGratisDesde.</summary>
    public static decimal CostoEnvio(StoreSettingsDto s, decimal subtotal) =>
        s.EnvioGratisDesde.HasValue && subtotal >= s.EnvioGratisDesde.Value ? 0 : Math.Max(0, s.CostoEnvio);
}
