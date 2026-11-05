using System.Security.Claims;

namespace UtilesApi.Services;

/// <summary>Roles que se usan en [Authorize(Roles = ...)]. Deben coincidir con el claim de rol del JWT (ClaimTypes.Role).</summary>
public static class AppRoles
{
    public const string Admin = "ADMIN";
    public const string Operator = "OPERATOR";
    public const string User = "USER";
    /// <summary>Administración de la tienda: ADMIN u OPERATOR.</summary>
    public const string Staff = "ADMIN,OPERATOR";
}

public static class ClaimsPrincipalExtensions
{
    /// <summary>Id del usuario logueado (claim nameidentifier del token) o null si no hay sesión.</summary>
    public static Guid? GetUserId(this ClaimsPrincipal? user)
    {
        if (user?.Identity?.IsAuthenticated != true) return null;
        var value = user.FindFirst(ClaimTypes.NameIdentifier)?.Value
                    ?? user.FindFirst("nameid")?.Value
                    ?? user.FindFirst("sub")?.Value;
        return Guid.TryParse(value, out var id) ? id : null;
    }

    public static bool IsStaff(this ClaimsPrincipal? user) =>
        user?.Identity?.IsAuthenticated == true && (user.IsInRole(AppRoles.Admin) || user.IsInRole(AppRoles.Operator));

    public static bool IsAdmin(this ClaimsPrincipal? user) =>
        user?.Identity?.IsAuthenticated == true && user.IsInRole(AppRoles.Admin);
}
