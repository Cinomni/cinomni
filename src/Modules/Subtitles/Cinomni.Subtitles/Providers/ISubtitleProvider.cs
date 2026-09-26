using Cinomni.Subtitles.Contracts;

namespace Cinomni.Subtitles.Providers;

/// <summary>
/// What to look for: the release name to match against, the wanted language/HI, and — for an episode —
/// the series identity and its numbering.
/// <para>
/// The series members are trailing optional and a movie leaves every one of them null. They exist
/// because a release name alone is a bad search key for an episode: a provider fed only
/// <c>The.Wire.S02E05.1080p.WEB-DL.x264-GRP</c> returns roughly the same broad candidate set for every
/// episode of the show, and the <c>MinScore</c> gate then happily accepts a subtitle for the wrong
/// episode. <see cref="SeasonNumber"/>/<see cref="EpisodeNumber"/> narrow the query at the provider
/// instead of hoping the scorer notices.
/// </para>
/// </summary>
/// <param name="SeriesTitle">The catalog title of the series (the search term a provider matches shows on).</param>
/// <param name="ParentImdbId">The <em>series</em> IMDb id, when the catalog knows one — the strongest key a provider offers.</param>
public sealed record SubtitleProviderQuery(
    string Release,
    string Language,
    bool HearingImpaired,
    string? SeriesTitle = null,
    int? SeasonNumber = null,
    int? EpisodeNumber = null,
    string? ParentImdbId = null,
    bool Forced = false)
{
    /// <summary>Whether this query is scoped to one episode (both numbers known), rather than a movie.</summary>
    public bool IsEpisode => SeasonNumber is not null && EpisodeNumber is not null;
}

/// <summary>A scored candidate a provider returned, with an opaque handle to download it.</summary>
public sealed record ProviderCandidate(
    string Release,
    int Score,
    bool HearingImpaired,
    SubtitleFormat Format,
    string DownloadRef,
    bool Forced = false);

/// <summary>The downloaded subtitle content and its format.</summary>
public sealed record ProviderDownload(byte[] Content, SubtitleFormat Format);

/// <summary>
/// Port to an external subtitle provider (OpenSubtitles REST for the MVP). The production
/// adapter fetches over an SSRF-hardened client and treats the response as untrusted; tests substitute
/// a mock. Providers are throttled and their content validated before it lands.
/// </summary>
public interface ISubtitleProvider
{
    string Name { get; }

    /// <summary>
    /// Whether this provider can actually reach its service — for OpenSubtitles, whether an API key was
    /// configured. A provider that cannot call out is skipped entirely, so it costs neither an HTTP
    /// request nor a slot of the outbound-call stagger: the default install ships the adapter with no
    /// credentials, and paying a rate limit for a call that never happens is pure latency.
    /// </summary>
    bool IsConfigured { get; }

    Task<IReadOnlyList<ProviderCandidate>> SearchAsync(SubtitleProviderQuery query, CancellationToken cancellationToken = default);

    Task<ProviderDownload> DownloadAsync(string downloadRef, CancellationToken cancellationToken = default);
}
