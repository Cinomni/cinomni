using Cinomni.ReleaseParsing.Contracts;

namespace Cinomni.ReleaseParsing.Persistence;

/// <summary>
/// A parsed release: the deterministic interpretation of one source title. The quality value-object,
/// revision and languages are stored as <c>jsonb</c> (a controlled value, not scattered domain); the
/// canonical key is a queryable scalar for cross-indexer identity.
/// <para>
/// This is a re-derivable cache with a retention window (<c>Retention:ReleaseParsing:ParseRetention</c>),
/// not the durable explanation. The parse is a pure function of the title and the parser version, and
/// the row is a get-or-create keyed on the title, so a purged parse is simply re-created the next time
/// the title is seen. The explanation of why a release was taken lives where it is used, in Decision's
/// evaluation reasons, which carry their own profile and actual values and are kept indefinitely for
/// an accepted verdict or a manual override.
/// </para>
/// </summary>
public sealed class ParsedReleaseRecord
{
    public Guid Id { get; init; }

    public required string SourceTitle { get; init; }

    public ReleaseType ReleaseType { get; init; }

    /// <summary>jsonb: <c>{ source, resolution, modifier }</c>.</summary>
    public required string QualityJson { get; init; }

    /// <summary>jsonb: <c>{ version, real, isRepack }</c>.</summary>
    public required string RevisionJson { get; init; }

    /// <summary>jsonb: <c>[ "English", … ]</c>.</summary>
    public required string LanguagesJson { get; init; }

    public string? ReleaseGroup { get; init; }

    public string? Edition { get; init; }

    public int? Year { get; init; }

    /// <summary>jsonb: the full <c>EpisodeNumbering</c>, or null for a movie.</summary>
    public string? NumberingJson { get; init; }

    /// <summary>Queryable projection of the numbering: the season, or null when there is none.</summary>
    public int? Season { get; init; }

    /// <summary>Last season of a multi-season pack (<c>S01-S03</c>); null for a single season.</summary>
    public int? SeasonTo { get; init; }

    /// <summary>First episode covered by the release; equal to <see cref="EpisodeTo"/> for a single episode.</summary>
    public int? EpisodeFrom { get; init; }

    /// <summary>Last episode covered by the release.</summary>
    public int? EpisodeTo { get; init; }

    /// <summary>First absolute (anime) episode number, when the release is numbered that way.</summary>
    public int? AbsoluteEpisode { get; init; }

    /// <summary>Air date of a date-numbered episode, as published by the provider (no time zone).</summary>
    public DateOnly? AirDate { get; init; }

    /// <summary><c>Part N</c> suffix, when present.</summary>
    public int? Part { get; init; }

    public required string CanonicalKey { get; init; }

    public string? InfoHash { get; init; }

    public required string ParserVersion { get; init; }

    public DateTimeOffset CreatedAt { get; init; }
}
