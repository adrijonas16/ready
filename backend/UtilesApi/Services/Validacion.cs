using System.Text.Json;

namespace UtilesApi.Services;

public static class Validacion
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>Teléfono peruano o internacional: solo dígitos, espacios, +, -, paréntesis; 6 a 15 dígitos.</summary>
    public static bool TelefonoValido(string? telefono)
    {
        if (string.IsNullOrWhiteSpace(telefono)) return false;
        if (telefono.Any(c => !(char.IsDigit(c) || c == ' ' || c == '+' || c == '-' || c == '(' || c == ')'))) return false;
        var digitos = telefono.Count(char.IsDigit);
        return digitos >= 6 && digitos <= 15;
    }

    /// <summary>DNI (8 dígitos), RUC (11 dígitos) o carné de extranjería (hasta 12 caracteres alfanuméricos).</summary>
    public static bool DocumentoValido(string? documento)
    {
        if (string.IsNullOrWhiteSpace(documento)) return false;
        var d = documento.Trim();
        if (d.All(char.IsDigit)) return d.Length == 8 || d.Length == 11 || (d.Length >= 9 && d.Length <= 12);
        return d.Length <= 12 && d.All(char.IsLetterOrDigit);
    }

    public static bool RucValido(string? ruc) => !string.IsNullOrWhiteSpace(ruc) && ruc.Trim().Length == 11 && ruc.Trim().All(char.IsDigit);

    private static readonly TimeZoneInfo Lima = BuscarZonaLima();

    private static TimeZoneInfo BuscarZonaLima()
    {
        try { return TimeZoneInfo.FindSystemTimeZoneById("America/Lima"); }
        catch
        {
            try { return TimeZoneInfo.FindSystemTimeZoneById("SA Pacific Standard Time"); }
            catch { return TimeZoneInfo.CreateCustomTimeZone("Lima", TimeSpan.FromHours(-5), "Lima", "Lima"); }
        }
    }

    /// <summary>
    /// Fechas para columnas timestamptz (Npgsql solo acepta UTC). Una fecha sin zona ("2026-03-01")
    /// se toma como hora de Lima; si es fin de rango y viene sin hora, se usa el final de ese día.
    /// </summary>
    public static DateTime? AUtc(DateTime? fecha, bool finDeDia = false)
    {
        if (fecha == null) return null;
        var f = fecha.Value;
        switch (f.Kind)
        {
            case DateTimeKind.Utc: return f;
            case DateTimeKind.Local: return f.ToUniversalTime();
            default:
                if (finDeDia && f.TimeOfDay == TimeSpan.Zero) f = f.AddDays(1).AddTicks(-1);
                return TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(f, DateTimeKind.Unspecified), Lima);
        }
    }

    /// <summary>
    /// Igual que AUtc pero en texto ISO-8601 (UTC) para usar con ::timestamptz / ::timestamp en SQL.
    /// Así no dependemos de cómo Dapper/Npgsql infieren el tipo de un DateTime.
    /// </summary>
    public static string? AUtcTexto(DateTime? fecha, bool finDeDia = false) =>
        AUtc(fecha, finDeDia)?.ToString("yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'", System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>Lee una fecha ISO del cliente. Sin zona queda Unspecified (se toma como hora de Lima en AUtc).</summary>
    public static DateTime? LeerFecha(string? texto)
    {
        if (string.IsNullOrWhiteSpace(texto)) return null;
        return DateTime.TryParse(texto.Trim(), System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.RoundtripKind, out var f) ? f : null;
    }

    public static string? ASerializar<T>(T? valor) where T : class =>
        valor == null ? null : JsonSerializer.Serialize(valor, Json);

    public static T? Leer<T>(string? json) where T : class
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try { return JsonSerializer.Deserialize<T>(json, Json); }
        catch { return null; }
    }
}
