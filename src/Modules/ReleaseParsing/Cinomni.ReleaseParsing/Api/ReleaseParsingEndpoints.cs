using Cinomni.Kernel.Security;
using Cinomni.ReleaseParsing.Contracts;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Cinomni.ReleaseParsing.Api;

/// <summary>HTTP surface of the Release Parsing module: parse a release name on demand. Requires authentication.</summary>
public static class ReleaseParsingEndpoints
{
    public static IEndpointRouteBuilder MapReleaseParsingEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/parsing").RequireAuthorization(AuthorizationPolicies.Administrator);

        group.MapPost("/parse", (ParseRequest request, IReleaseParser parser) =>
        {
            var result = parser.Parse(request.Title);
            return result.IsSuccess
                ? Results.Ok(ToDto(result.Value))
                : Results.Json(new { error = result.Error.Code, message = result.Error.Message },
                    statusCode: StatusCodes.Status422UnprocessableEntity);
        });

        return endpoints;
    }

    private static object ToDto(ParsedRelease parsed) => new
    {
        sourceTitle = parsed.SourceTitle,
        releaseType = parsed.ReleaseType.ToString(),
        quality = new
        {
            source = parsed.Quality.Source.ToString(),
            resolution = parsed.Quality.Resolution.ToString(),
            modifier = parsed.Quality.Modifier.ToString(),
        },
        revision = new
        {
            version = parsed.Revision.Version,
            real = parsed.Revision.Real,
            isRepack = parsed.Revision.IsRepack,
        },
        languages = parsed.Languages,
        releaseGroup = parsed.ReleaseGroup,
        edition = parsed.Edition,
        year = parsed.Year,
        numbering = ToNumberingDto(parsed.Numbering),
        canonicalKey = parsed.Identity.CanonicalKey,
        infoHash = parsed.Identity.InfoHash,
        parserVersion = parsed.ParserVersion,
    };

    /// <summary>Null for a movie; the series numbering block otherwise.</summary>
    private static object? ToNumberingDto(EpisodeNumbering? numbering) => numbering is null
        ? null
        : new
        {
            season = numbering.Season,
            seasonTo = numbering.SeasonTo,
            episodes = numbering.Episodes,
            absoluteEpisodes = numbering.AbsoluteEpisodes,
            airDate = numbering.AirDate,
            isComplete = numbering.IsComplete,
            part = numbering.Part,
        };

    public sealed record ParseRequest(string Title);
}
