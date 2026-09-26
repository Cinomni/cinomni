using System.Text.Json;
using System.Text.Json.Serialization;
using Cinomni.ReleaseParsing.Contracts;

namespace Cinomni.ReleaseParsing.Persistence;

/// <summary>Builds a <see cref="ParsedReleaseRecord"/> from a parse result, serializing the value-objects to jsonb.</summary>
internal static class ParsedReleaseRecords
{
    // camelCase keys ({ source, resolution, modifier }); enum values keep their name ("Bluray").
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() },
    };

    public static ParsedReleaseRecord Create(ParsedRelease parsed, Guid id, DateTimeOffset createdAt)
    {
        var numbering = parsed.Numbering;

        return new ParsedReleaseRecord
        {
            Id = id,
            SourceTitle = parsed.SourceTitle,
            ReleaseType = parsed.ReleaseType,
            QualityJson = JsonSerializer.Serialize(parsed.Quality, JsonOptions),
            RevisionJson = JsonSerializer.Serialize(parsed.Revision, JsonOptions),
            LanguagesJson = JsonSerializer.Serialize(parsed.Languages, JsonOptions),
            NumberingJson = numbering is null ? null : JsonSerializer.Serialize(numbering, JsonOptions),
            Season = numbering?.Season,
            SeasonTo = numbering?.SeasonTo,
            EpisodeFrom = First(numbering?.Episodes),
            EpisodeTo = Last(numbering?.Episodes),
            AbsoluteEpisode = First(numbering?.AbsoluteEpisodes),
            AirDate = numbering?.AirDate,
            Part = numbering?.Part,
            ReleaseGroup = Fit(parsed.ReleaseGroup, ReleaseGroupMaxLength),
            Edition = Fit(parsed.Edition, EditionMaxLength),
            Year = parsed.Year,
            CanonicalKey = Fit(parsed.Identity.CanonicalKey, CanonicalKeyMaxLength)!,
            InfoHash = Fit(parsed.Identity.InfoHash, InfoHashMaxLength),
            ParserVersion = parsed.ParserVersion,
            CreatedAt = createdAt,
        };
    }

    /// <summary>Column widths, shared with the model. The parser accepts titles of 1000 characters, and
    /// a key or a group derived from one can be longer than its column.</summary>
    internal const int ReleaseGroupMaxLength = 100;

    internal const int EditionMaxLength = 100;

    internal const int CanonicalKeyMaxLength = 500;

    internal const int InfoHashMaxLength = 100;

    /// <summary>Cut to <paramref name="maxLength"/>, never between the halves of a surrogate pair.</summary>
    private static string? Fit(string? value, int maxLength)
    {
        if (value is null || value.Length <= maxLength)
        {
            return value;
        }

        var cut = char.IsHighSurrogate(value[maxLength - 1]) ? maxLength - 1 : maxLength;
        return value[..cut];
    }

    private static int? First(IReadOnlyList<int>? numbers) => numbers is { Count: > 0 } ? numbers[0] : null;

    private static int? Last(IReadOnlyList<int>? numbers) => numbers is { Count: > 0 } ? numbers[^1] : null;
}
