using System.Globalization;
using System.Text;
using UtilesApi.DTOs;

namespace UtilesApi.Services;

/// <summary>
/// Dónde vende la tienda. Mientras Configuración &gt; "Envíos a todo el Perú" esté apagado, solo Arequipa:
/// la tienda muestra solo colegios de Arequipa y no acepta pedidos con entrega en otro departamento.
/// </summary>
public static class Cobertura
{
    public const string Base = "Arequipa";

    /// <summary>Los 24 departamentos más la Provincia Constitucional del Callao.</summary>
    public static readonly string[] Departamentos =
    {
        "Amazonas", "Áncash", "Apurímac", "Arequipa", "Ayacucho", "Cajamarca", "Callao", "Cusco",
        "Huancavelica", "Huánuco", "Ica", "Junín", "La Libertad", "Lambayeque", "Lima", "Loreto",
        "Madre de Dios", "Moquegua", "Pasco", "Piura", "Puno", "San Martín", "Tacna", "Tumbes", "Ucayali"
    };

    private static readonly Dictionary<string, string> PorClave =
        Departamentos.ToDictionary(Clave, d => d);

    private static string Clave(string texto)
    {
        var sb = new StringBuilder();
        foreach (var ch in texto.Trim().ToLowerInvariant().Normalize(NormalizationForm.FormD))
            if (CharUnicodeInfo.GetUnicodeCategory(ch) != UnicodeCategory.NonSpacingMark && !char.IsWhiteSpace(ch)) sb.Append(ch);
        return sb.ToString();
    }

    /// <summary>Nombre oficial del departamento ("cusco", "CUSCO", "Cusco " → "Cusco"), o null si no existe.</summary>
    public static string? Normalizar(string? departamento) =>
        string.IsNullOrWhiteSpace(departamento) ? null : PorClave.GetValueOrDefault(Clave(departamento));

    /// <summary>¿Se puede vender/entregar en ese departamento con la configuración actual?</summary>
    public static bool Atiende(StoreSettingsDto s, string? departamento)
    {
        var d = Normalizar(departamento);
        return d != null && (s.CoberturaNacional || d == Base);
    }
}
