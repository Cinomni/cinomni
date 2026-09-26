using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Cinomni.Host.SystemInfo;

/// <summary>
/// What this installation is running. One read-only route, and deliberately not on <c>/health</c>:
/// the probes are anonymous so an orchestrator can reach them without credentials, and a build
/// identity is not something to hand to anyone who can reach the port.
/// <para>
/// Signed in is the whole bar, with no administrator gate. A version footer exists so that a member
/// reporting a problem can say which build they are on, and one only an administrator can read does
/// not do that job. Neither the number nor the commit is a secret in a repository that ships its
/// source; what would be a secret — paths, endpoints, credentials — is not here.
/// </para>
/// </summary>
public static class SystemEndpoints
{
    public static IEndpointRouteBuilder MapSystemEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/system").RequireAuthorization();

        group.MapGet("/info", () => Results.Ok(ToDto(BuildInfo.Current)));

        return endpoints;
    }

    internal static object ToDto(BuildInfo build) => new
    {
        version = build.Version,
        informationalVersion = build.InformationalVersion,
        // Null means the build was not stamped, which is every development build. It is not an error
        // and not an unknown: a client is expected to leave the row out rather than render a blank.
        commit = build.Commit,
        buildDate = build.BuildDate,
    };
}
