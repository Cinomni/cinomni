using Cinomni.Metadata.Contracts;
using Cinomni.Metadata.Providers;

namespace Cinomni.Metadata.Tests;

/// <summary>
/// In-memory <see cref="IMetadataSource"/> that scripts the search hits and the fetched snapshot (with
/// artwork), so the module's orchestration (search → fetch → select artwork → persist → emit) is tested
/// deterministically without a live provider. The real REST clients are a deployment concern, exercised
/// outside the unit tests. Name and supported kinds are configurable so multi-provider routing can be
/// tested with several fakes.
/// </summary>
internal sealed class FakeMetadataSource : IMetadataSource
{
    public FakeMetadataSource(string name = "tmdb", MetadataMediaKind kind = MetadataMediaKind.Movie)
    {
        Name = name;
        SupportedKinds = new HashSet<MetadataMediaKind> { kind };
    }

    public string Name { get; }

    public IReadOnlySet<MetadataMediaKind> SupportedKinds { get; }

    /// <summary>
    /// Whether this provider is configured well enough to answer. Set to <c>false</c> to stand in for a
    /// provider with no API key — the state a freshly installed Cinomni is in, where a search must come
    /// back as unanswered rather than as an empty result.
    /// </summary>
    public bool IsAvailable { get; set; } = true;

    /// <summary>How many times orchestration asked this provider to explain why it was skipped.</summary>
    public int Announcements { get; private set; }

    public List<ProviderMetadataCandidate> Candidates { get; set; } =
        [new ProviderMetadataCandidate("603", "The Matrix", 1999, "A hacker learns the truth about his reality.")];

    /// <summary>
    /// The snapshot returned by <see cref="FetchAsync"/>; set to <c>null</c> to simulate an unavailable
    /// provider. The artwork set exercises the selection policy: the English poster (lower vote) must beat
    /// the Spanish one (higher vote) on language priority.
    /// </summary>
    public ProviderMetadataResult? Result { get; set; } = new(
        "603", "The Matrix", "The Matrix", 1999, "A hacker learns the truth about his reality.", 136, "en",
        PosterEnUrl, BackdropUrl, "{\"id\":603,\"title\":\"The Matrix\"}",
        [
            new ProviderArtwork(ArtworkKind.Poster, PosterEnUrl, "en", 2000, 3000, VoteAverage: 8.0, VoteCount: 100),
            new ProviderArtwork(ArtworkKind.Poster, PosterEsUrl, "es", 2000, 3000, VoteAverage: 9.0, VoteCount: 50),
            new ProviderArtwork(ArtworkKind.Backdrop, BackdropUrl, null, 3840, 2160, VoteAverage: 7.0, VoteCount: 30),
        ]);

    public const string PosterEnUrl = "https://image.example/poster-en.jpg";
    public const string PosterEsUrl = "https://image.example/poster-es.jpg";
    public const string BackdropUrl = "https://image.example/backdrop.jpg";

    public const string SeriesExternalId = "121361";
    public const string SeasonOnePosterUrl = "https://image.example/season-1.jpg";
    public const string EpisodeStillUrl = "https://image.example/s01e01.jpg";

    /// <summary>A series-shaped source with the preset structure already loaded.</summary>
    public static FakeMetadataSource ForSeries(string name = "tvdb", SeriesStatus status = SeriesStatus.Continuing) =>
        new(name, MetadataMediaKind.Series)
        {
            Candidates = [new ProviderMetadataCandidate(SeriesExternalId, "The Frontier", 2002, "A synthetic series.")],
            Result = SeriesResult(status),
        };

    /// <summary>
    /// The series preset every orchestration test scripts through: two seasons (one with its own poster),
    /// four episodes spanning the specials bucket, absolute numbering, air dates and an episode still,
    /// plus the same series-level artwork set the movie preset uses so the language policy stays under
    /// test. Callers override <see cref="ProviderMetadataResult.Series"/> for a different structure.
    /// </summary>
    public static ProviderMetadataResult SeriesResult(
        SeriesStatus status = SeriesStatus.Continuing,
        IReadOnlyList<ProviderSeason>? seasons = null,
        IReadOnlyList<ProviderEpisode>? episodes = null,
        string seasonOrder = SeasonOrders.Official) => new(
        SeriesExternalId,
        "The Frontier",
        "The Frontier",
        2002,
        "A synthetic series.",
        60,
        "en",
        PosterEnUrl,
        BackdropUrl,
        "{\"id\":121361,\"name\":\"The Frontier\"}",
        [
            new ProviderArtwork(ArtworkKind.Poster, PosterEnUrl, "en", 2000, 3000, VoteAverage: 8.0, VoteCount: 100),
            new ProviderArtwork(ArtworkKind.Poster, PosterEsUrl, "es", 2000, 3000, VoteAverage: 9.0, VoteCount: 50),
            new ProviderArtwork(ArtworkKind.Backdrop, BackdropUrl, null, 3840, 2160, VoteAverage: 7.0, VoteCount: 30),
        ],
        new ProviderSeriesDetails(
            status,
            new DateOnly(2002, 6, 2),
            status == SeriesStatus.Ended ? new DateOnly(2008, 3, 9) : null,
            seasonOrder,
            new ProviderExternalIds("121361", "tt0306414", "1438"),
            seasons ?? DefaultSeasons,
            episodes ?? DefaultEpisodes));

    public static readonly IReadOnlyList<ProviderSeason> DefaultSeasons =
    [
        new ProviderSeason(1, "Season One", "The first season.", 2, new DateOnly(2002, 6, 2), SeasonOnePosterUrl, "s1"),
        new ProviderSeason(2, "Season Two", null, 1, new DateOnly(2003, 6, 1), null, "s2"),
    ];

    public static readonly IReadOnlyList<ProviderEpisode> DefaultEpisodes =
    [
        new ProviderEpisode(0, 1, "Prologue", null, null, new DateOnly(2002, 5, 1), null, 15, null, "e0", IsSpecial: true),
        new ProviderEpisode(1, 1, "The Target", "A synthetic episode.", 1, new DateOnly(2002, 6, 2), new DateTimeOffset(2002, 6, 3, 1, 0, 0, TimeSpan.Zero), 60, EpisodeStillUrl, "e1", IsSpecial: false),
        new ProviderEpisode(1, 2, "The Detail", null, 2, new DateOnly(2002, 6, 9), null, 60, null, "e2", IsSpecial: false),
        new ProviderEpisode(2, 1, "Ebb Tide", null, 3, new DateOnly(2003, 6, 1), null, 59, null, "e3", IsSpecial: false),
    ];

    public List<MetadataProviderQuery> Searches { get; } = [];

    public List<string> Fetched { get; } = [];

    public void AnnounceUnavailable() => Announcements++;

    public Task<IReadOnlyList<ProviderMetadataCandidate>> SearchAsync(MetadataProviderQuery query, CancellationToken cancellationToken = default)
    {
        Searches.Add(query);
        return Task.FromResult<IReadOnlyList<ProviderMetadataCandidate>>(Candidates);
    }

    /// <summary>Thrown by <see cref="FetchAsync"/> when set: a provider that fails rather than answering.</summary>
    public Exception? FetchException { get; set; }

    public Task<ProviderMetadataResult?> FetchAsync(string externalId, MetadataMediaKind kind, CancellationToken cancellationToken = default)
    {
        Fetched.Add(externalId);
        return FetchException is { } failure
            ? Task.FromException<ProviderMetadataResult?>(failure)
            : Task.FromResult(Result);
    }
}
