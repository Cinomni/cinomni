using Cinomni.Catalog.Contracts;
using Cinomni.Kernel.Identifiers;
using Cinomni.Kernel.Security;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Cinomni.Catalog.Tests;

/// <summary>
/// Integration tests against a real PostgreSQL instance for the descriptive fields a work carries to
/// every viewer (synopsis, runtime, genres) and for the paged catalog list.
/// </summary>
public sealed class WorkDescriptionAndPagingTests : IAsyncLifetime
{
    private const int OverviewMaxLength = 8000;

    private static readonly Viewer Member = new(Uuid7.New(), IsAdministrator: false);

    private ServiceProvider _provider = null!;

    public async Task InitializeAsync() => _provider = await CatalogTestHost.CreateAsync("cinomni_test_catalog_description");

    public async Task DisposeAsync() => await _provider.DisposeAsync();

    [Fact]
    public async Task A_snapshot_gives_every_viewer_the_synopsis_runtime_and_genres()
    {
        // Arrange
        var workId = await AddMovieAsync("The Matrix", "603");

        // Act
        await AttachAsync(workId, overview: "  A hacker learns what the world really is.  ", runtimeMinutes: 136,
            genres: ["Action", "Science Fiction"]);

        // Assert — read as a plain member, who may not read the snapshot itself.
        var work = await GetAsync(Member, workId);
        Assert.NotNull(work);
        Assert.Equal("A hacker learns what the world really is.", work.Overview);
        Assert.Equal(136, work.RuntimeMinutes);
        Assert.Equal(["Action", "Science Fiction"], work.Genres);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task A_later_snapshot_without_a_synopsis_keeps_the_earlier_one(string? overview)
    {
        // Arrange
        var workId = await AddMovieAsync("The Matrix", "603");
        await AttachAsync(workId, overview: "A hacker learns what the world really is.");

        // Act
        await AttachAsync(workId, overview: overview);

        // Assert — a provider that says nothing has not said the work is about nothing.
        Assert.Equal("A hacker learns what the world really is.", (await GetAsync(Member, workId))!.Overview);
    }

    [Fact]
    public async Task An_oversized_synopsis_is_clamped_to_the_column()
    {
        // Arrange
        var workId = await AddMovieAsync("The Matrix", "603");

        // Act
        await AttachAsync(workId, overview: new string('x', OverviewMaxLength + 500));

        // Assert
        Assert.Equal(OverviewMaxLength, (await GetAsync(Member, workId))!.Overview!.Length);
    }

    [Fact]
    public async Task Pages_walk_the_visible_works_in_title_order_with_their_total()
    {
        // Arrange — five visible works, and one on a shelf the member was never granted.
        string[] titles = ["Brazil", "Alien", "Heat", "Casablanca", "Dune"];
        for (var i = 0; i < titles.Length; i++)
        {
            await AddMovieAsync(titles[i], $"{100 + i}");
        }

        var hidden = await CreateRestrictedShelfAsync("Grown-ups");
        await AddMovieAsync("Arrival", "999", hidden);

        // Act
        var first = await PageAsync(Member, offset: 0, limit: 2);
        var second = await PageAsync(Member, offset: 2, limit: 2);
        var last = await PageAsync(Member, offset: 4, limit: 2);
        var beyond = await PageAsync(Member, offset: 10, limit: 2);

        // Assert — the hidden work is neither listed nor counted.
        Assert.Equal(["Alien", "Brazil"], first.Items.Select(w => w.Title));
        Assert.Equal(["Casablanca", "Dune"], second.Items.Select(w => w.Title));
        Assert.Equal(["Heat"], last.Items.Select(w => w.Title));
        Assert.Empty(beyond.Items);
        Assert.All([first, second, last, beyond], page => Assert.Equal(5, page.Total));
    }

    [Theory]
    [InlineData(0, CatalogPaging.DefaultPageSize)]
    [InlineData(-3, CatalogPaging.DefaultPageSize)]
    [InlineData(100_000, CatalogPaging.MaxPageSize)]
    [InlineData(7, 7)]
    public async Task A_page_size_is_clamped(int requested, int applied)
    {
        var page = await PageAsync(Member, offset: 0, limit: requested);

        Assert.Equal(applied, page.Limit);
    }

    [Fact]
    public async Task A_negative_offset_reads_as_the_start()
    {
        await AddMovieAsync("Alien", "348");

        var page = await PageAsync(Member, offset: -10, limit: 5);

        Assert.Equal(0, page.Offset);
        Assert.Equal("Alien", Assert.Single(page.Items).Title);
    }

    [Fact]
    public async Task A_page_narrows_by_kind_title_availability_and_genre_together()
    {
        // Arrange — a movie on disk and one not; a series fully, partly and not at all on disk.
        var matrix = await AddMovieAsync("The Matrix", "603");
        await MarkAvailableAsync(matrix);
        await AttachAsync(matrix, genres: ["Action", "Science Fiction"]);
        var heat = await AddMovieAsync("Heat", "949");
        await AttachAsync(heat, genres: ["Crime"]);
        await AddSeriesAsync("Dark", "81189", episodes: 2, available: 2);
        await AddSeriesAsync("The Wire", "79126", episodes: 3, available: 1);
        await AddSeriesAsync("Severance", "371980", episodes: 2, available: 0);

        // Act + Assert — each filter alone, then together. Sort titles drop the leading article.
        Assert.Equal(["Dark", "Severance", "The Wire"], await TitlesAsync(new WorkListQuery(Kind: WorkKind.Series)));
        Assert.Equal(["Dark", "The Matrix"], await TitlesAsync(new WorkListQuery(Availability: WorkAvailability.Complete)));
        Assert.Equal(["The Wire"], await TitlesAsync(new WorkListQuery(Availability: WorkAvailability.Partial)));
        Assert.Equal(["Heat", "Severance"], await TitlesAsync(new WorkListQuery(Availability: WorkAvailability.None)));
        Assert.Equal(["The Matrix"], await TitlesAsync(new WorkListQuery(Genre: "Science Fiction")));
        Assert.Equal(["The Matrix", "The Wire"], await TitlesAsync(new WorkListQuery(Term: "  the ")));
        Assert.Equal(
            ["The Wire"],
            await TitlesAsync(new WorkListQuery(Kind: WorkKind.Series, Term: "THE", Availability: WorkAvailability.Partial)));
        Assert.Empty(await TitlesAsync(new WorkListQuery(Genre: "science fiction"))); // a label, matched exactly
    }

    [Theory]
    [InlineData("%", true)]
    [InlineData("_", false)]
    [InlineData("\\", false)]
    public async Task A_title_search_takes_wildcards_literally(string term, bool findsTheWolf)
    {
        await AddMovieAsync("Alien", "348");
        await AddMovieAsync("100% Wolf", "1");

        var titles = await TitlesAsync(new WorkListQuery(Term: term));

        Assert.Equal(findsTheWolf ? ["100% Wolf"] : [], titles);
    }

    [Fact]
    public async Task Each_order_is_total_and_stable()
    {
        // Arrange — added in this order; two share a year, one is undated.
        await AttachAsync(await AddMovieAsync("Brazil", "1"), year: 1985);
        await AttachAsync(await AddMovieAsync("Alien", "2"), year: 1979);
        await AttachAsync(await AddMovieAsync("Aliens", "3"), year: 1986);
        await AttachAsync(await AddMovieAsync("Clue", "4"), year: 1985);
        await AddMovieAsync("Untitled", "5");

        // Act + Assert
        Assert.Equal(["Alien", "Aliens", "Brazil", "Clue", "Untitled"], await TitlesAsync(new WorkListQuery()));
        Assert.Equal(
            ["Aliens", "Brazil", "Clue", "Alien", "Untitled"],
            await TitlesAsync(new WorkListQuery(Sort: WorkSort.Year)));
        Assert.Equal(
            ["Untitled", "Clue", "Aliens", "Alien", "Brazil"],
            await TitlesAsync(new WorkListQuery(Sort: WorkSort.Added)));
    }

    [Fact]
    public async Task Facets_count_kinds_and_genres_the_viewer_may_see()
    {
        // Arrange — two movies, a series, and a movie on a shelf the member was never granted.
        await AttachAsync(await AddMovieAsync("The Matrix", "603"), genres: ["Action", "Science Fiction"]);
        await AttachAsync(await AddMovieAsync("Arrival", "329865"), genres: ["Science Fiction", "Drama"]);
        await AttachAsync(await AddSeriesAsync("Dark", "81189", episodes: 0, available: 0), genres: ["Drama"]);
        var hidden = await CreateRestrictedShelfAsync("Grown-ups");
        await AttachAsync(await AddMovieAsync("Alien", "348", hidden), genres: ["Horror"]);

        // Act
        var all = await FacetsAsync(Member);
        var movies = await FacetsAsync(Member, WorkKind.Movie);

        // Assert — the hidden movie and its genre count for nobody who may not see it.
        Assert.Equal(2, all.Movies);
        Assert.Equal(1, all.Series);
        Assert.Equal(
            [new GenreCount("Drama", 2), new GenreCount("Science Fiction", 2), new GenreCount("Action", 1)],
            all.Genres);
        Assert.Equal(
            [new GenreCount("Science Fiction", 2), new GenreCount("Action", 1), new GenreCount("Drama", 1)],
            movies.Genres);
    }

    // -- helpers ---------------------------------------------------------------------------------

    private async Task<Guid> AddMovieAsync(string title, string externalId, CollectionId? collection = null)
    {
        await using var scope = _provider.CreateAsyncScope();
        var added = await scope.ServiceProvider.GetRequiredService<ICatalogCommands>().AddMovieAsync(
            title, null, [new ExternalId(MetadataProvider.Tmdb, externalId)], collection);
        Assert.True(added.IsSuccess, added.IsFailure ? added.Error.Message : null);
        return added.Value.Value;
    }

    private async Task<CollectionId> CreateRestrictedShelfAsync(string name)
    {
        await using var scope = _provider.CreateAsyncScope();
        var created = await scope.ServiceProvider.GetRequiredService<ICollectionAdministration>()
            .CreateAsync(name, CollectionKind.Movies, CollectionAccessMode.Restricted);
        Assert.True(created.IsSuccess, created.IsFailure ? created.Error.Message : null);
        return created.Value;
    }

    /// <summary>Applies a fresh snapshot, as the Metadata reaction does; every call is a new snapshot id.</summary>
    private async Task AttachAsync(
        Guid workId,
        string? overview = null,
        int? runtimeMinutes = null,
        IReadOnlyList<string>? genres = null,
        int? year = null)
    {
        await using var scope = _provider.CreateAsyncScope();
        var current = await scope.ServiceProvider.GetRequiredService<ICatalogBrowse>()
            .GetByIdAsync(new Viewer(Uuid7.New(), IsAdministrator: true), new WorkId(workId));
        Assert.NotNull(current);
        await scope.ServiceProvider.GetRequiredService<ICatalogCommands>().AttachMetadataSnapshotAsync(
            workId,
            Uuid7.New(),
            current.Title,
            originalLanguage: "en",
            year: year,
            runtimeMinutes,
            posterUrl: null,
            backdropUrl: null,
            genres: genres,
            overview: overview);
    }

    private async Task<WorkSummary?> GetAsync(Viewer viewer, Guid workId)
    {
        await using var scope = _provider.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<ICatalogBrowse>().GetByIdAsync(viewer, new WorkId(workId));
    }

    private async Task<WorkPage> PageAsync(Viewer viewer, int offset, int limit, WorkListQuery? query = null)
    {
        await using var scope = _provider.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<ICatalogBrowse>()
            .PageAsync(viewer, query ?? new WorkListQuery(), offset, limit);
    }

    private async Task<IReadOnlyList<string>> TitlesAsync(WorkListQuery query)
    {
        var page = await PageAsync(Member, 0, CatalogPaging.MaxPageSize, query);
        Assert.Equal(page.Items.Count, page.Total);
        return page.Items.Select(w => w.Title).ToList();
    }

    private async Task<WorkFacets> FacetsAsync(Viewer viewer, WorkKind? kind = null)
    {
        await using var scope = _provider.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<ICatalogBrowse>().FacetsAsync(viewer, null, kind);
    }

    /// <summary>A series with <paramref name="episodes"/> episodes, the first <paramref name="available"/> of them on disk.</summary>
    private async Task<Guid> AddSeriesAsync(string title, string externalId, int episodes, int available)
    {
        await using var scope = _provider.CreateAsyncScope();
        var commands = scope.ServiceProvider.GetRequiredService<ICatalogCommands>();
        var added = await commands.AddSeriesAsync(title, null, [new ExternalId(MetadataProvider.Tvdb, externalId)]);
        Assert.True(added.IsSuccess, added.IsFailure ? added.Error.Message : null);
        var workId = added.Value;
        if (episodes == 0)
        {
            return workId.Value;
        }

        await commands.SyncSeriesStructureAsync(workId.Value, new SeriesStructure(
            Uuid7.New(),
            "tvdb",
            [new SeasonStructureInput(1)],
            [.. Enumerable.Range(1, episodes).Select(n => new EpisodeStructureInput(1, n))]));

        var episodeList = await scope.ServiceProvider.GetRequiredService<ICatalogSeriesQuery>().GetEpisodesAsync(workId, 1);
        foreach (var episode in episodeList.Take(available))
        {
            await commands.MarkEpisodeAvailableAsync(episode.Id.Value, Uuid7.New(), Uuid7.New());
        }

        return workId.Value;
    }

    private async Task MarkAvailableAsync(Guid workId)
    {
        await using var scope = _provider.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<ICatalogCommands>().MarkWorkAvailableAsync(workId, Uuid7.New());
    }
}
