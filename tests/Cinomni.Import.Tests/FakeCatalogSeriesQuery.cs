using Cinomni.Catalog.Contracts;
using Cinomni.Kernel.Identifiers;

namespace Cinomni.Import.Tests;

/// <summary>
/// In-memory <see cref="ICatalogQuery"/>/<see cref="ICatalogSeriesQuery"/> so the import pipeline's
/// file→episode resolution is tested deterministically without the Catalog schema. Only the members
/// Import actually calls are scripted; the rest throw, so a new dependency cannot slip in unnoticed.
/// </summary>
internal sealed class FakeCatalogSeriesQuery : ICatalogQuery, ICatalogSeriesQuery
{
    private readonly Dictionary<Guid, WorkSummary> _works = [];
    private readonly List<EpisodeSummary> _episodes = [];
    private readonly List<SeasonSummary> _seasons = [];

    /// <summary>Registers a movie work, so the movie path resolves to itself.</summary>
    public Guid SeedMovie(string title = "Test Movie", int? year = 2024)
    {
        var id = Uuid7.New();
        _works[id] = new WorkSummary(new WorkId(id), WorkKind.Movie, title, year, WorkStatus.Released, false, []);
        return id;
    }

    /// <summary>Registers a series work with one season and <paramref name="episodeCount"/> episodes.</summary>
    public (Guid WorkId, IReadOnlyList<Guid> EpisodeIds) SeedSeries(
        string title,
        int year,
        int seasonNumber,
        int episodeCount)
    {
        var workId = Uuid7.New();
        _works[workId] = new WorkSummary(
            new WorkId(workId), WorkKind.Series, title, year, WorkStatus.Released, false, []);

        var seasonId = Uuid7.New();
        _seasons.Add(new SeasonSummary(new SeasonId(seasonId), new WorkId(workId), seasonNumber, null, null, null, null));

        var episodeIds = new List<Guid>();
        for (var number = 1; number <= episodeCount; number++)
        {
            var episodeId = Uuid7.New();
            episodeIds.Add(episodeId);
            _episodes.Add(new EpisodeSummary(
                new EpisodeId(episodeId),
                new SeasonId(seasonId),
                new WorkId(workId),
                seasonNumber,
                number,
                AbsoluteNumber: number,
                Title: $"Episode {number}",
                AirDate: new DateOnly(year, 1, 1).AddDays(number),
                AirDateTime: null,
                RuntimeMinutes: 60,
                HasAsset: false));
        }

        return (workId, episodeIds);
    }

    /// <summary>
    /// Publishes <paramref name="secondNumber"/> under the air date of <paramref name="firstNumber"/>,
    /// as a provider does for a two-part finale released on one night. The episodes index on air date
    /// is not unique, so this is a shape the real catalog can hold.
    /// </summary>
    public void ShareAirDate(Guid workId, int firstNumber, int secondNumber)
    {
        var first = _episodes.Single(e => e.WorkId.Value == workId && e.Number == firstNumber);
        var index = _episodes.FindIndex(e => e.WorkId.Value == workId && e.Number == secondNumber);
        _episodes[index] = _episodes[index] with { AirDate = first.AirDate };
    }

    /// <summary>The air date the fake gave an episode when it seeded the series.</summary>
    public DateOnly AirDateOf(Guid workId, int number) =>
        _episodes.Single(e => e.WorkId.Value == workId && e.Number == number).AirDate!.Value;

    public Task<WorkSummary?> GetByIdAsync(WorkId id, CancellationToken cancellationToken = default) =>
        Task.FromResult(_works.TryGetValue(id.Value, out var work) ? work : null);

    public Task<WorkSummary?> FindByExternalIdAsync(
        MetadataProvider provider, string value, WorkKind? kind = null, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("Import never resolves a work by external id.");

    public Task<IReadOnlyList<WorkSummary>> ListAsync(CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("Import never lists the catalog.");

    public Task<IReadOnlyList<EpisodeSummary>> ResolveEpisodeAsync(
        WorkId workId, int seasonNumber, int episodeNumber, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<EpisodeSummary>>(
            [.. Of(workId).Where(e => e.SeasonNumber == seasonNumber && e.Number == episodeNumber)]);

    public Task<IReadOnlyList<EpisodeSummary>> ResolveEpisodeRangeAsync(
        WorkId workId, int seasonNumber, int firstEpisodeNumber, int lastEpisodeNumber, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<EpisodeSummary>>(
            [.. Of(workId)
                .Where(e => e.SeasonNumber == seasonNumber && e.Number >= firstEpisodeNumber && e.Number <= lastEpisodeNumber)
                .OrderBy(e => e.Number)]);

    public Task<IReadOnlyList<EpisodeSummary>> ResolveSeasonAsync(
        WorkId workId, int seasonNumber, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<EpisodeSummary>>(
            [.. Of(workId).Where(e => e.SeasonNumber == seasonNumber).OrderBy(e => e.Number)]);

    public Task<IReadOnlyList<EpisodeSummary>> ResolveByAbsoluteNumberAsync(
        WorkId workId, int absoluteNumber, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<EpisodeSummary>>(
            [.. Of(workId).Where(e => e.AbsoluteNumber == absoluteNumber)]);

    public Task<IReadOnlyList<EpisodeSummary>> ResolveByAirDateAsync(
        WorkId workId, DateOnly airDate, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<EpisodeSummary>>([.. Of(workId).Where(e => e.AirDate == airDate)]);

    public Task<IReadOnlyList<SeasonSummary>> GetSeasonsAsync(WorkId workId, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<SeasonSummary>>(
            [.. _seasons.Where(s => s.WorkId == workId).OrderBy(s => s.Number)]);

    public Task<IReadOnlyList<EpisodeSummary>> GetEpisodesAsync(
        WorkId workId, int seasonNumber, CancellationToken cancellationToken = default) =>
        ResolveSeasonAsync(workId, seasonNumber, cancellationToken);

    public Task<IReadOnlyList<EpisodeSummary>> GetAllEpisodesAsync(WorkId workId, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<EpisodeSummary>>(
            [.. Of(workId).OrderBy(e => e.SeasonNumber).ThenBy(e => e.Number)]);

    public Task<EpisodeSummary?> GetEpisodeAsync(
        WorkId workId, EpisodeId episodeId, CancellationToken cancellationToken = default) =>
        Task.FromResult(Of(workId).FirstOrDefault(e => e.Id == episodeId));

    private IEnumerable<EpisodeSummary> Of(WorkId workId) => _episodes.Where(e => e.WorkId == workId);
}
