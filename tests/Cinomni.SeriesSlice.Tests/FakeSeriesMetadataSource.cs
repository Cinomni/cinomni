using Cinomni.Metadata.Contracts;
using Cinomni.Metadata.Providers;

namespace Cinomni.SeriesSlice.Tests;

/// <summary>
/// The single metadata provider of the acceptance host: it answers with a scripted series structure so
/// the whole chain — refresh → snapshot → catalog structure → monitored targets — runs on the real code
/// path without reaching a live provider.
/// <para>
/// The structure is <b>mutable</b> on purpose. A later refresh returning one more season is exactly how
/// a continuing show behaves, and it is the case the permanently-consumed <c>apply-policy:{workId}</c>
/// idempotency key used to swallow.
/// </para>
/// </summary>
internal sealed class FakeSeriesMetadataSource : IMetadataSource
{
    /// <summary>The provider name; also the structure claim recorded on the work.</summary>
    public const string ProviderName = "tvdb";

    public const string SeriesExternalId = "121361";

    public const string SeriesTitle = "The Frontier";

    public const int SeriesYear = 2002;

    /// <summary>TMDB id of the movie the regression fact catalogues (it needs no refresh of its own).</summary>
    public const string MovieExternalId = "603";

    public const string MovieTitle = "The Matrix";

    public const int MovieYear = 1999;

    private static readonly DateOnly LongAgo = new(2012, 3, 1);

    private ProviderMetadataResult _result = SeriesResult([SeasonOf(1, episodes: 4)]);

    public string Name => ProviderName;

    public IReadOnlySet<MetadataMediaKind> SupportedKinds { get; } =
        new HashSet<MetadataMediaKind> { MetadataMediaKind.Series };

    /// <summary>Every external id this fake was asked to fetch, in arrival order.</summary>
    public List<string> Fetched { get; } = [];

    /// <summary>Replaces the scripted answer, modelling a provider that has learnt about a new season.</summary>
    public void ScriptSeries(params SeasonScript[] seasons) => _result = SeriesResult(seasons);

    public Task<IReadOnlyList<ProviderMetadataCandidate>> SearchAsync(
        MetadataProviderQuery query,
        CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<ProviderMetadataCandidate>>(
            [new ProviderMetadataCandidate(SeriesExternalId, SeriesTitle, SeriesYear, "A synthetic series.")]);

    public Task<ProviderMetadataResult?> FetchAsync(
        string externalId,
        MetadataMediaKind kind,
        CancellationToken cancellationToken = default)
    {
        Fetched.Add(externalId);
        return Task.FromResult<ProviderMetadataResult?>(_result);
    }

    /// <summary>One season of <paramref name="episodes"/> episodes, all long since aired.</summary>
    public static SeasonScript SeasonOf(int number, int episodes) => new(number, episodes);

    private static ProviderMetadataResult SeriesResult(IReadOnlyList<SeasonScript> seasons) => new(
        SeriesExternalId,
        SeriesTitle,
        SeriesTitle,
        SeriesYear,
        "A synthetic series.",
        60,
        "en",
        PosterUrl: null,
        BackdropUrl: null,
        RawJson: "{\"id\":121361}",
        Artwork: [],
        Series: new ProviderSeriesDetails(
            SeriesStatus.Continuing,
            new DateOnly(SeriesYear, 6, 2),
            LastAired: null,
            SeasonOrders.Official,
            new ProviderExternalIds(SeriesExternalId, "tt0306414", "1438"),
            [.. seasons.Select(s => new ProviderSeason(
                s.Number, $"Season {s.Number}", null, s.Episodes, LongAgo, null, $"s{s.Number}"))],
            [.. seasons.SelectMany(EpisodesOf)]));

    private static IEnumerable<ProviderEpisode> EpisodesOf(SeasonScript season) =>
        Enumerable.Range(1, season.Episodes).Select(number => new ProviderEpisode(
            season.Number,
            number,
            $"Episode {number}",
            Overview: null,
            AbsoluteNumber: (season.Number - 1) * 100 + number,
            // Long past, so the unaired gate never suppresses the search.
            LongAgo,
            AirDateTime: null,
            RuntimeMinutes: 60,
            StillUrl: null,
            ExternalId: $"s{season.Number}e{number}",
            IsSpecial: season.Number == 0));

    /// <summary>One scripted season: its number and how many episodes it carries.</summary>
    internal readonly record struct SeasonScript(int Number, int Episodes);
}
