using System.Security.Claims;

namespace Cinomni.Kernel.Security;

/// <summary>
/// Who is reading, resolved from the session's claims — never from the request. The id decides what was
/// granted to them and what counts as theirs; the administrator flag is the bypass.
/// <para>
/// It lives in the kernel because more than one module's contracts name it, and a <c>.Contracts</c>
/// project may not reference another one — the same reason <see cref="AuthorizationPolicies"/>
/// is here. A read model that answers for a person takes one of these; the unscoped twin of such a read
/// model exists only for event and command handlers, which have no person to answer for.
/// </para>
/// </summary>
public readonly record struct Viewer(
    Guid UserId,
    bool IsAdministrator,
    string? ContentCeiling = null,
    string? ContentCeilingRegion = null)
{
    /// <summary>
    /// Reads the viewer off an authenticated principal. Null when it carries no usable id, which an
    /// endpoint answers with 401 rather than guessing.
    /// </summary>
    public static Viewer? From(ClaimsPrincipal principal)
    {
        // FindFirst rather than the FindFirstValue extension: that one ships with ASP.NET Core, and the
        // kernel depends on nothing.
        if (!Guid.TryParse(principal.FindFirst(ClaimTypes.NameIdentifier)?.Value, out var id))
        {
            return null;
        }

        var ceiling = principal.FindFirst(AuthorizationClaims.ContentCeiling)?.Value;
        var ceilingRegion = principal.FindFirst(AuthorizationClaims.ContentCeilingRegion)?.Value;
        return new Viewer(
            id,
            principal.FindFirst(AuthorizationClaims.Administrator)?.Value == "true",
            string.IsNullOrWhiteSpace(ceiling) ? null : ceiling,
            string.IsNullOrWhiteSpace(ceilingRegion) ? null : ceilingRegion);
    }
}
