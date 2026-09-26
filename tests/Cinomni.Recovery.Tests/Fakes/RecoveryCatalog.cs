using Cinomni.Catalog.Contracts;
using Cinomni.Import.Contracts;
using Cinomni.Import.Probe;
using Cinomni.Kernel.Identifiers;

namespace Cinomni.Recovery.Tests.Fakes;

/// <summary>
/// The catalogue as far as Import needs it: enough to resolve a staged file to the episode it covers
/// and to name where it lands. Catalog's own schema and the metadata chain that fills it are proven
/// by their own suites — this suite is about what survives a restart, and standing the whole
/// cataloguing half up again would double its runtime without asserting anything more about that.
/// <para>
/// Only the members Import calls are answered; the rest throw, so a new dependency cannot slip in
/// behind a fake that quietly returns nothing.
/// </para>
/// </summary>
internal sealed class RecoveryCatalog : ICatalogQuery, ICatalogSeriesQuery
{
    private readonly Dictionary<Guid, WorkSummary> _works = [];
    private readonly List<SeasonSummary> _seasons = [];
    private readonly List<EpisodeSummary> _episodes = [];

    public Guid SeedMovie(string title, int year)
    {
        var id = Uuid7.New();
        _works[id] = new WorkSummary(new WorkId(id), WorkKind.Movie, title, year, WorkStatus.Released, false, []);
        return id;
    }

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
        _seasons.Add(new SeasonSummary(
            new SeasonId(seasonId), new WorkId(workId), seasonNumber, null, null, null, null));

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
                RuntimeMinutes: 45,
                HasAsset: false));
        }

        return (workId, episodeIds);
    }

    public Task<WorkSummary?> GetByIdAsync(WorkId id, CancellationToken cancellationToken = default) =>
        Task.FromResult(_works.GetValueOrDefault(id.Value));

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
        WorkId workId,
        int seasonNumber,
        int firstEpisodeNumber,
        int lastEpisodeNumber,
        CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<EpisodeSummary>>(
            [.. Of(workId)
                .Where(e => e.SeasonNumber == seasonNumber
                    && e.Number >= firstEpisodeNumber
                    && e.Number <= lastEpisodeNumber)
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

    public Task<IReadOnlyList<SeasonSummary>> GetSeasonsAsync(
        WorkId workId, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<SeasonSummary>>(
            [.. _seasons.Where(s => s.WorkId == workId).OrderBy(s => s.Number)]);

    public Task<IReadOnlyList<EpisodeSummary>> GetEpisodesAsync(
        WorkId workId, int seasonNumber, CancellationToken cancellationToken = default) =>
        ResolveSeasonAsync(workId, seasonNumber, cancellationToken);

    public Task<IReadOnlyList<EpisodeSummary>> GetAllEpisodesAsync(
        WorkId workId, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<EpisodeSummary>>(
            [.. Of(workId).OrderBy(e => e.SeasonNumber).ThenBy(e => e.Number)]);

    public Task<EpisodeSummary?> GetEpisodeAsync(
        WorkId workId, EpisodeId episodeId, CancellationToken cancellationToken = default) =>
        Task.FromResult(Of(workId).FirstOrDefault(e => e.Id == episodeId));

    private IEnumerable<EpisodeSummary> Of(WorkId workId) => _episodes.Where(e => e.WorkId == workId);
}

/// <summary>
/// A media probe that always answers the same playable file. The recovery suite asserts on how many
/// files were analysed and registered, never on their streams, so a scripted-per-file probe would add
/// noise without adding a fact.
/// </summary>
internal sealed class RecoveryMediaProbe : IMediaProbe
{
    private static readonly MediaInfo Result = new(
        "matroska,webm",
        DurationSeconds: 2700,
        Bitrate: 6_000_000,
        Streams:
        [
            new MediaStreamInfo(0, MediaStreamKind.Video, "h264", null, 1920, 1080, null, IsDefault: true, IsForced: false),
            new MediaStreamInfo(1, MediaStreamKind.Audio, "aac", "eng", null, null, 2, IsDefault: true, IsForced: false),
        ]);

    /// <summary>Every path analysed, across the whole run including restarts.</summary>
    public List<string> Probed { get; } = [];

    public Task<MediaInfo> ProbeAsync(string filePath, CancellationToken cancellationToken = default)
    {
        Probed.Add(filePath);
        return Task.FromResult(Result);
    }
}
