using Cinomni.Catalog.Contracts;
using Cinomni.Catalog.Persistence;
using Cinomni.Kernel.Identifiers;
using Cinomni.Operations.Messaging;
using Cinomni.Operations.Transactions;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Cinomni.Catalog.Application;

/// <summary>
/// Materialises a provider snapshot's season/episode tree onto a series and announces it with
/// <see cref="SeriesStructureChanged"/>.
/// <para>
/// Three rules govern this class and none of them is negotiable:
/// </para>
/// <list type="number">
/// <item>
/// <b>It never deletes.</b> Seasons are upserted by <c>(work, number)</c> and episodes by
/// <c>(work, season number, number)</c>. Providers renumber specials and reorder episodes between
/// snapshots; a delete-then-insert would mint new ids and orphan every asset link, acquisition intent and
/// monitored target pointing at the removed row.
/// </item>
/// <item>
/// <b>An empty snapshot is a no-op, not a wipe.</b> A provider outage that returns zero episodes must
/// leave the catalog exactly as it was.
/// </item>
/// <item>
/// <b>A bulk absolute-number shift settles in one sync</b> (<see cref="AbsoluteNumberPlan"/>). Refusing
/// the values another episode still holds would free one episode per refresh and leave anime releases
/// resolving to the wrong episode for as long as it took to converge.
/// </item>
/// <item>
/// <b>The first provider whose structure lands claims the numbering</b> (<c>Work.StructureProvider</c>).
/// The same provider re-syncs; a different one is skipped entirely here and only enriches descriptively
/// through <c>AttachMetadataSnapshot</c>. Two providers with different orderings would otherwise fight
/// over the same <c>SxxEyy</c> slots and silently re-point episodes that releases already match against.
/// </item>
/// </list>
/// </summary>
public sealed class SeriesStructureService(
    CatalogDbContext dbContext,
    IUnitOfWork unitOfWork,
    IEventBus eventBus)
{
    /// <summary>
    /// Upserts <paramref name="structure"/> onto the series and publishes <see cref="SeriesStructureChanged"/>
    /// in one unit of work. Idempotent: re-running the same snapshot creates nothing and changes no counts.
    /// </summary>
    public async Task SyncAsync(Guid workId, SeriesStructure structure, CancellationToken cancellationToken = default)
    {
        var provider = Text.Truncate(structure.Provider.Trim(), Work.StructureProviderMaxLength);
        if (string.IsNullOrEmpty(provider))
        {
            // A blank provider could never be matched again, so it must not be allowed to claim the work.
            return;
        }

        if (structure.Seasons.Count == 0 && structure.Episodes.Count == 0)
        {
            return; // nothing to say, and definitely nothing to remove
        }

        try
        {
            await SyncOnceAsync(workId, structure, provider, cancellationToken);
        }
        catch (DbUpdateException ex) when (IsNaturalKeyConflict(ex))
        {
            // A concurrent sync of the same work won the race on a natural key. Drop our pending inserts
            // and converge on the winner's rows — the second pass finds them and updates instead, exactly
            // as MonitoringCommands.ApplyMonitoringPolicyAsync converges on a duplicate target.
            dbContext.ChangeTracker.Clear();
            await SyncOnceAsync(workId, structure, provider, cancellationToken);
        }
    }

    private async Task SyncOnceAsync(
        Guid workId,
        SeriesStructure structure,
        string provider,
        CancellationToken cancellationToken)
    {
        var work = await dbContext.Works.FirstOrDefaultAsync(w => w.Id == workId, cancellationToken);
        if (work is null || work.Kind != WorkKind.Series)
        {
            return; // unknown work (inter-schema ref, no FK) or a movie — a movie has no structure
        }

        // Rule 3: a snapshot from a provider that did not claim the numbering enriches nothing here.
        if (work.StructureProvider is not null &&
            !string.Equals(work.StructureProvider, provider, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var seasonsByNumber = await LoadSeasonsAsync(workId, cancellationToken);
        var episodes = await dbContext.Episodes.Where(e => e.WorkId == workId).ToListAsync(cancellationToken);

        var createdSeasonIds = UpsertSeasons(workId, structure, seasonsByNumber);
        var (createdEpisodeIds, absoluteNumbers) = UpsertEpisodes(workId, structure, seasonsByNumber, episodes);

        await CommitAsync(
            work, structure, provider, createdSeasonIds, createdEpisodeIds, absoluteNumbers, cancellationToken);
    }

    /// <summary>
    /// Adds the seasons the snapshot mentions but the work does not have yet, then copies the snapshot's
    /// descriptive fields onto all of them. Returns the ids actually created.
    /// </summary>
    private List<Guid> UpsertSeasons(
        Guid workId,
        SeriesStructure structure,
        Dictionary<int, Season> seasonsByNumber)
    {
        var now = DateTimeOffset.UtcNow;
        var created = new List<Guid>();

        // Every season number the snapshot mentions, including ones only an episode names: a provider may
        // publish an episode list without a matching season entry, and every episode needs a parent.
        foreach (var number in SeasonNumbers(structure))
        {
            if (seasonsByNumber.ContainsKey(number))
            {
                continue;
            }

            var season = new Season { Id = Uuid7.New(), WorkId = workId, Number = number, AddedAt = now };
            dbContext.Seasons.Add(season);
            seasonsByNumber[number] = season;
            created.Add(season.Id);
        }

        foreach (var input in structure.Seasons)
        {
            seasonsByNumber[input.Number].ApplyMetadata(
                input.Title, input.AirDate, input.ExpectedEpisodeCount, input.PosterUrl, structure.SnapshotId);
        }

        return created;
    }

    /// <summary>
    /// Upserts every episode of the snapshot by its natural key <c>(work, season number, number)</c>.
    /// Episodes the snapshot no longer mentions are simply left alone — never removed. Returns the ids
    /// actually created and the absolute-number reassignment the commit has to write in two phases.
    /// </summary>
    private (List<Guid> CreatedEpisodeIds, AbsoluteNumberPlan AbsoluteNumbers) UpsertEpisodes(
        Guid workId,
        SeriesStructure structure,
        Dictionary<int, Season> seasonsByNumber,
        List<Episode> storedEpisodes)
    {
        var now = DateTimeOffset.UtcNow;
        var byKey = storedEpisodes.ToDictionary(e => (e.SeasonNumber, e.Number));
        var created = new List<Guid>();

        foreach (var input in structure.Episodes)
        {
            var key = (input.SeasonNumber, input.Number);
            if (!byKey.TryGetValue(key, out var episode))
            {
                episode = new Episode
                {
                    Id = Uuid7.New(),
                    SeasonId = seasonsByNumber[input.SeasonNumber].Id,
                    WorkId = workId,
                    SeasonNumber = input.SeasonNumber,
                    Number = input.Number,
                    AddedAt = now,
                };
                dbContext.Episodes.Add(episode);
                byKey[key] = episode;
                created.Add(episode.Id);
            }

            // The absolute number is deliberately withheld here (null leaves the stored one untouched):
            // it is the one field whose unique index makes a naive per-episode write unsatisfiable, so
            // the whole reassignment is planned across the work and applied in two phases at commit.
            episode.ApplyMetadata(
                input.Title,
                absoluteNumber: null,
                input.AirDate,
                input.AirDateTime,
                input.RuntimeMinutes,
                input.StillUrl,
                structure.SnapshotId);
        }

        return (created, AbsoluteNumberPlan.For(structure, byKey));
    }

    /// <summary>Commits the upsert, the provider claim and the rollup together with the announcement.</summary>
    private Task CommitAsync(
        Work work,
        SeriesStructure structure,
        string provider,
        IReadOnlyList<Guid> createdSeasonIds,
        IReadOnlyList<Guid> createdEpisodeIds,
        AbsoluteNumberPlan absoluteNumbers,
        CancellationToken cancellationToken) =>
        unitOfWork.ExecuteAsync(async token =>
        {
            work.StructureProvider = provider;
            await ApplyAbsoluteNumbersAsync(absoluteNumbers, token);

            // Recount from the rows inside the transaction rather than adding up what this pass created:
            // the rollup then stays correct even when an earlier sync was interrupted.
            var seasonCount = await dbContext.Seasons.CountAsync(s => s.WorkId == work.Id, token);
            work.EpisodeCount = await dbContext.Episodes.CountAsync(e => e.WorkId == work.Id, token);
            await dbContext.SaveChangesAsync(token);

            await eventBus.PublishAsync(
                new SeriesStructureChanged(
                    work.Id,
                    structure.SnapshotId,
                    provider,
                    seasonCount,
                    work.EpisodeCount,
                    createdSeasonIds,
                    createdEpisodeIds),
                token);
        }, cancellationToken);

    /// <summary>
    /// Writes the upsert and, with it, the absolute-number reassignment — releases before assignments,
    /// with a flush in between, because <c>ux_episodes_work_absolute_number</c> is not deferrable and
    /// every value a shifted ordering assigns is still held by a neighbour until the release lands.
    /// Both statements run inside the caller's unit of work, so the whole shift is one transaction.
    /// </summary>
    private async Task ApplyAbsoluteNumbersAsync(AbsoluteNumberPlan plan, CancellationToken cancellationToken)
    {
        foreach (var episode in plan.Released)
        {
            episode.AbsoluteNumber = null;
            episode.AbsoluteNumberIsDerived = false;
        }

        // Also the flush that lands the upserted rows themselves (new episodes come in with no number).
        await dbContext.SaveChangesAsync(cancellationToken);

        if (plan.Assigned.Count == 0)
        {
            return;
        }

        foreach (var (episode, number, derived) in plan.Assigned)
        {
            episode.AbsoluteNumber = number;
            episode.AbsoluteNumberIsDerived = derived;
        }

        await dbContext.SaveChangesAsync(cancellationToken);
    }

    private async Task<Dictionary<int, Season>> LoadSeasonsAsync(Guid workId, CancellationToken cancellationToken)
    {
        var seasons = await dbContext.Seasons.Where(s => s.WorkId == workId).ToListAsync(cancellationToken);
        return seasons.ToDictionary(s => s.Number);
    }

    /// <summary>Every season number the snapshot mentions, whether as a season entry or via an episode.</summary>
    private static IEnumerable<int> SeasonNumbers(SeriesStructure structure) =>
        structure.Seasons.Select(s => s.Number)
            .Concat(structure.Episodes.Select(e => e.SeasonNumber))
            .Distinct();

    /// <summary>True when the failure is a duplicate natural key (a concurrent sync of the same work).</summary>
    private static bool IsNaturalKeyConflict(DbUpdateException ex) =>
        ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation };
}
