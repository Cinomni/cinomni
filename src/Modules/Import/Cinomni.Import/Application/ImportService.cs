using System.Diagnostics;
using Cinomni.Catalog.Contracts;
using Cinomni.Import.Contracts;
using Cinomni.Import.Diagnostics;
using Cinomni.Import.Files;
using Cinomni.Import.Persistence;
using Cinomni.Import.Probe;
using Cinomni.Kernel.Diagnostics;
using Cinomni.Kernel.Messaging;
using Cinomni.Library.Contracts;
using Cinomni.Operations.Messaging;
using Cinomni.Operations.Settings;
using Cinomni.Operations.Transactions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Cinomni.Import.Application;

/// <summary>
/// Drives the import job lifecycle for a completed download. Every out-of-process side effect (scan,
/// fingerprint, hardlink, verify, ffprobe) happens <b>before</b> the unit of work; the aggregate
/// change and its integration events are then written together in one transaction. The
/// whole run is idempotent and recoverable: keyed by the download task, a redelivery reuses the
/// existing job, and a file that already verified is skipped rather than re-linked (case #15).
/// <para>
/// The drive is a <b>loop over the planned files</b>, because a season pack is one download of many
/// episodes. Two publication rules follow from that and are not interchangeable:
/// </para>
/// <list type="bullet">
///   <item><description>
///     <b>one <c>MediaAvailable</c> per landed file</b>, carrying that file's own units and numbers —
///     per-episode assets are what make per-episode subtitles, playback and resume work at all;
///   </description></item>
///   <item><description>
///     <b>exactly one <c>ImportCompleted</c> per job</b> — Acquisition's goal is per intent, so N of
///     them would drive N goal completions for a single intent.
///   </description></item>
/// </list>
/// <para>
/// A batch that loses part of itself is <b>not</b> discarded: every file that landed and verified is
/// registered and announced, the ones that did not are recorded with their reason, and the job
/// returns to <c>Pending</c> with an <c>ImportFailed</c> so the acquisition goal keeps moving.
/// Withholding the nine files a ten-file pack landed leaves them as orphans inside the library tree
/// that nothing knows about and nothing can announce later.
/// </para>
/// </summary>
public sealed class ImportService(
    ImportDbContext dbContext,
    IUnitOfWork unitOfWork,
    IEventBus eventBus,
    IImportFileSystem fileSystem,
    IMediaProbe mediaProbe,
    ImportPolicy policy,
    TargetResolution targetResolution,
    LibraryOrganizer organizer,
    ILibraryQuery library,
    ReleaseQualityReader qualityReader,
    ImportOptions options,
    ILogger<ImportService> logger,
    ILiveOptions<MovieNamingOptions>? naming = null) : IImportProcessor
{
    /// <summary>Library paths compare the way the host filesystem does.</summary>
    private static readonly StringComparer TargetPathComparer = OperatingSystem.IsWindows()
        ? StringComparer.OrdinalIgnoreCase
        : StringComparer.Ordinal;

    /// <summary>The reason recorded, and sent to Acquisition, when a content path is not a folder of its own.</summary>
    internal const string ContentPathNotInStaging = "The download's content path is not a folder inside the staging area.";

    /// <summary>
    /// Runs one import and records how long it took and how it ended.
    /// <para>
    /// This is the longest synchronous stretch of an acquisition — scan, fingerprint, hardlink, probe —
    /// so it is the one an operator experiences as "the download finished ages ago and it is still not
    /// in the library". No path is ever recorded: a staging path is private layout and a library path
    /// spells out the title.
    /// </para>
    /// </summary>
    public async Task ProcessCompletedDownloadAsync(
        Guid downloadTaskId,
        Guid intentId,
        Guid attemptId,
        Guid workId,
        Guid targetId,
        string contentPath,
        IReadOnlyList<Guid>? unitIds = null,
        CancellationToken cancellationToken = default)
    {
        using var activity = CinomniTelemetry.Source.StartActivity("import.job", ActivityKind.Internal);
        activity?.SetTag(CinomniTelemetry.Tags.Module, CinomniTelemetry.Modules.Import);
        // The job's own id, so a trace can be tied back to a row an administrator may read. Spans only:
        // an identifier on a metric would make its cardinality unbounded.
        activity?.SetTag(CinomniTelemetry.Tags.EntityId, downloadTaskId);

        var startedAt = Stopwatch.GetTimestamp();
        var outcome = await RunAsync(
            downloadTaskId, intentId, attemptId, workId, targetId, contentPath, unitIds, cancellationToken);

        if (outcome is null)
        {
            // A redelivery that did nothing. Counting it would inflate every import figure by however
            // many times the outbox happened to retry.
            return;
        }

        activity?.SetTag(CinomniTelemetry.Tags.Outcome, outcome);
        ImportMetrics.RecordJob(outcome, Stopwatch.GetElapsedTime(startedAt));
    }

    /// <summary>
    /// The import itself. Returns the terminal outcome, or <see langword="null"/> when nothing ran
    /// because the job was already done.
    /// </summary>
    private async Task<string?> RunAsync(
        Guid downloadTaskId,
        Guid intentId,
        Guid attemptId,
        Guid workId,
        Guid targetId,
        string contentPath,
        IReadOnlyList<Guid>? unitIds,
        CancellationToken cancellationToken)
    {
        var existing = await LoadByDownloadAsync(downloadTaskId, cancellationToken);
        if (existing is not null && existing.State is not ImportJobState.Pending)
        {
            // Registered/Rejected (done) or Unmatched (awaiting a manual import) — a redelivered
            // DownloadCompleted must not re-drive it. Only a fresh or rolled-back-to-Pending job runs.
            return null;
        }

        var now = DateTimeOffset.UtcNow;
        var job = existing ?? await OpenJobAsync(
            downloadTaskId, intentId, attemptId, workId, targetId, contentPath, unitIds, now, cancellationToken);

        // A re-drive of an existing job takes the scope the job recorded when it was opened, not the
        // one this call happened to carry. Startup recovery has no hand-off to echo, and resolving an
        // unconstrained scope would land the episodes a season pack ships that nobody asked for.
        unitIds ??= job.RequestedUnitIds;

        // Mount guard: a caved-in library root must not be written to — leave the job recoverable
        // (Pending) rather than fail the goal (guard, don't write).
        if (!fileSystem.RootAccessible(options.LibraryRoot))
        {
            logger.LogWarning("Library root {Root} is not accessible; deferring import job {JobId}.", options.LibraryRoot, job.Id);
            return ImportMetrics.Outcomes.Deferred;
        }

        var work = await targetResolution.GetWorkAsync(job.WorkId, cancellationToken);
        var kind = work?.Kind ?? WorkKind.Movie;
        var drive = ImportDrive.Open(job);
        var events = new List<IDomainEvent>();

        job.BeginMatching(now);

        // Scanned only from a folder of its own inside the staging area. A content path that is the
        // staging root itself (a download whose name sanitised away to nothing) or lies outside it
        // (an edited or restored row) is refused before anything is read: scanning the root would
        // take every other download's files for this one.
        if (options.StagingRoot.Length > 0 && !PathGuard.IsStrictlyWithin(options.StagingRoot, contentPath))
        {
            logger.LogWarning(
                "Import job {JobId} was refused: its content path is not a folder inside the staging root. Nothing was scanned.",
                job.Id);
            job.MarkUnmatched(ContentPathNotInStaging, now);
            events.Add(new ImportFailed(job.Id, job.IntentId, job.AttemptId, ContentPathNotInStaging));
            await PersistDriveAsync(job, drive, events, cancellationToken);
            return ImportMetrics.Outcomes.Unmatched;
        }

        // -- Match (specs pick the files; the fingerprints make matching by content, not name) -----
        var plan = policy.Plan(fileSystem.EnumerateFiles(contentPath), kind, contentPath);
        if (!plan.Accepted || plan.Files.Count == 0)
        {
            job.MarkUnmatched(plan.Reason, now);
            events.Add(new ImportFailed(job.Id, job.IntentId, job.AttemptId, plan.Reason));
            await PersistDriveAsync(job, drive, events, cancellationToken);
            return ImportMetrics.Outcomes.Unmatched;
        }

        var candidates = new List<ImportFileCandidate>(plan.Files.Count);
        foreach (var file in plan.Files)
        {
            candidates.Add(new ImportFileCandidate(
                file.Path, file.Size, await fileSystem.ComputeFingerprintAsync(file.Path, cancellationToken)));
        }

        var matches = job.RecordMatches(candidates, now);

        // -- Decide (resolve each file to its units and to the path it will land at) ---------------
        var landings = await PlanLandingsAsync(
            job, work, kind, matches, plan.Files, unitIds, contentPath, cancellationToken);
        if (landings.Count == 0 && !job.Matches.Any(m => m.IsLanded))
        {
            var reason = FirstReason(job) ?? "no file resolved to a landable target";
            job.Reject(reason, now);
            events.Add(new ImportFailed(job.Id, job.IntentId, job.AttemptId, reason));
            await PersistDriveAsync(job, drive, events, cancellationToken);
            return ImportMetrics.Outcomes.Rejected;
        }

        // -- Operate (one recoverable operation per file that is not already in place) -------------
        job.Approve(now);
        var failures = await OperateAsync(job, landings, cancellationToken);

        if (!job.Matches.Any(m => m.IsLanded))
        {
            var reason = $"no file could be landed for job {job.Id}";
            job.MarkOperationFailed(reason, now);
            events.Add(new ImportFailed(job.Id, job.IntentId, job.AttemptId, reason));
            await PersistDriveAsync(job, drive, events, cancellationToken);
            return ImportMetrics.Outcomes.Deferred;
        }

        job.MarkOperated(now);

        // -- Probe + register ----------------------------------------------------------------------
        await ProbeAndAnnounceAsync(job, downloadTaskId, failures, landings.Count, now, events, cancellationToken);
        await PersistDriveAsync(job, drive, events, cancellationToken);

        // Read from the aggregate rather than assumed: a partially failed batch announces what landed
        // and returns to Pending for a re-drive, so this is not always a registration.
        return job.State is ImportJobState.Registered
            ? ImportMetrics.Outcomes.Registered
            : ImportMetrics.Outcomes.Deferred;
    }

    /// <summary>
    /// Probes every file this drive landed and announces it as an asset.
    /// <para>
    /// When part of the batch failed the job does <b>not</b> register: it returns to
    /// <c>Pending</c> with an <c>ImportFailed</c>, so the acquisition goal keeps moving and a
    /// re-drive replans only the files that failed. The files that did land are announced all the
    /// same — withholding them leaves real files inside the library tree that no module has ever
    /// heard of, and no later drive can announce them either, because they are already verified.
    /// </para>
    /// </summary>
    private async Task ProbeAndAnnounceAsync(
        ImportJob job,
        Guid downloadTaskId,
        int failures,
        int plannedLandings,
        DateTimeOffset now,
        List<IDomainEvent> events,
        CancellationToken cancellationToken)
    {
        foreach (var match in job.Matches.Where(m => m.State == ImportFileMatchState.Operated))
        {
            match.MarkProbed(await mediaProbe.ProbeAsync(match.TargetPath!, cancellationToken));
        }

        if (failures == 0)
        {
            AddRegistrationEvents(job, downloadTaskId, job.Register(now), events);
            return;
        }

        var reason = $"{failures} of {plannedLandings} files failed to verify";
        AddMediaAvailableEvents(job, downloadTaskId, job.RegisterProbedFiles(), events);
        job.MarkPartiallyRegistered(reason, now);
        events.Add(new ImportFailed(job.Id, job.IntentId, job.AttemptId, reason));
    }

    /// <summary>
    /// Resolves each newly matched file to its catalog units and to a confined library path. A file
    /// that already landed on an earlier drive is deliberately skipped: re-planning its operation is
    /// exactly the bug that re-links a whole season pack on retry.
    /// </summary>
    private async Task<List<PlannedLanding>> PlanLandingsAsync(
        ImportJob job,
        WorkSummary? work,
        WorkKind kind,
        IReadOnlyList<ImportFileMatch> matches,
        IReadOnlyList<ImportFileEntry> files,
        IReadOnlyList<Guid>? unitIds,
        string contentPath,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<Guid> scope = unitIds ?? [];
        var landings = new List<PlannedLanding>(matches.Count);

        // Every library path this job has already spoken for: the ones earlier drives landed, plus
        // the ones this drive plans. An episode path is composed only from catalog data, so two
        // files of one download that resolve to the same episode (a PROPER next to the original, or
        // an absolute-numbered file next to its SxxEyy twin) get an identical path — and landing
        // both silently replaces the first on disk and then fails Library's whole unit of work on
        // ux_media_versions_full_path.
        var claimed = new HashSet<string>(
            job.Matches.Where(m => m.IsLanded && m.TargetPath is not null).Select(m => m.TargetPath!),
            TargetPathComparer);

        for (var index = 0; index < matches.Count; index++)
        {
            var match = matches[index];
            var file = files[index];
            if (match.IsLanded)
            {
                logger.LogDebug("Import job {JobId} file {File} already verified; skipping.", job.Id, file.Path);
                continue;
            }

            var resolution = kind == WorkKind.Series
                ? await targetResolution.ResolveFileAsync(job.WorkId, file.Path, scope, contentPath, cancellationToken)
                : MovieUnits(job, unitIds);
            if (kind == WorkKind.Series && !resolution.IsResolved)
            {
                match.MarkUnresolved(resolution.Reason!);
                continue;
            }

            match.ResolveUnits(resolution.UnitIds, resolution.SeasonNumber, resolution.EpisodeNumbers);

            var (targetPath, refusal) = await TargetPathAsync(job, work, kind, resolution, file, cancellationToken);
            if (targetPath is null)
            {
                var reason = refusal ?? "resolved target path escapes the library root or exceeds its length limit";
                logger.LogWarning("Import job {JobId} refused a target path for {File}: {Reason}.", job.Id, file.Path, reason);
                match.MarkFailed(reason);
                continue;
            }

            if (!claimed.Add(targetPath))
            {
                var duplicate = $"another file of this download already lands at {Path.GetFileName(targetPath)}";
                logger.LogWarning("Import job {JobId} refused {File}: {Reason}.", job.Id, file.Path, duplicate);
                match.MarkFailed(duplicate);
                continue;
            }

            landings.Add(new PlannedLanding(match, file, targetPath));
        }

        return landings;
    }

    /// <summary>
    /// Where a file lands, or why it may not. Every title owns its folder — the first one under the
    /// library root — and a folder another title already has files in is not this title's to share:
    /// two works with the same title and year, most often, or two releases of the same name. Sharing it
    /// used to mean filing somebody else's film in the bin as if it were an older copy, then playing
    /// this one under that one's name. This title's files land in a folder of its own instead, tagged
    /// with its work, the same tag every time, so a re-drive and a later upgrade find it again.
    /// </summary>
    private async Task<(string? Path, string? Refusal)> TargetPathAsync(
        ImportJob job,
        WorkSummary? work,
        WorkKind kind,
        ResolvedUnits resolution,
        ImportFileEntry file,
        CancellationToken cancellationToken)
    {
        var plain = Compose(ownerTag: null);
        if (plain is null || !await FolderHeldByAnotherWorkAsync(plain, job, cancellationToken))
        {
            return (plain, null);
        }

        var tagged = Compose(LibraryOrganizer.OwnerTag(OwnerOf(job)));
        if (tagged is not null && !await FolderHeldByAnotherWorkAsync(tagged, job, cancellationToken))
        {
            logger.LogInformation(
                "Import job {JobId} lands a file under a name of its own: its plain library folder belongs to another title.",
                job.Id);
            return (tagged, null);
        }

        return (null, "its library folder already belongs to another title");

        string? Compose(string? ownerTag) => kind == WorkKind.Series
            ? organizer.BuildEpisodePath(NamingOf(work!, resolution), file.Path, ownerTag)
            : organizer.BuildMoviePath(
                file.Path, naming?.Current.Format ?? MovieNamingFormat.ReleaseName, work?.Title, work?.Year, ownerTag);
    }

    /// <summary>
    /// The identity a job's files are owned by: its work, or — for a job correlated to no work, which
    /// cannot share anything with anybody — the job itself.
    /// </summary>
    private static Guid OwnerOf(ImportJob job) => job.WorkId == Guid.Empty ? job.Id : job.WorkId;

    /// <summary>
    /// Whether another title has anything in the folder <paramref name="libraryPath"/> would land in.
    /// Import's own record of what it landed is asked first: it is written by the drive that landed the
    /// file, before the next import starts, whereas Library only hears of a file once the event has been
    /// relayed and its command run — and two downloads finishing together must not both see an empty
    /// folder. Library is asked too, for what only it knows: a file a path repair has since moved.
    /// </summary>
    private async Task<bool> FolderHeldByAnotherWorkAsync(string libraryPath, ImportJob job, CancellationToken cancellationToken)
    {
        if (organizer.OwnerFolderOf(libraryPath) is not { } folder)
        {
            return false;
        }

        var prefix = folder + Path.DirectorySeparatorChar;
        var owner = OwnerOf(job);
        var landedHere = await dbContext.Matches
            .AsNoTracking()
            .Where(m => m.TargetPath != null && m.TargetPath.StartsWith(prefix)
                && (m.State == ImportFileMatchState.Operated
                    || m.State == ImportFileMatchState.Probed
                    || m.State == ImportFileMatchState.Registered))
            .Join(dbContext.Jobs, m => m.ImportJobId, j => j.Id, (m, j) => new { j.Id, j.WorkId })
            .Select(j => j.WorkId == Guid.Empty ? j.Id : j.WorkId)
            .Distinct()
            .ToListAsync(cancellationToken);
        if (landedHere.Any(other => other != owner))
        {
            return true;
        }

        var registered = await library.FindActiveWorksUnderAsync(folder, cancellationToken);
        return registered.Any(other => other != owner);
    }

    /// <summary>
    /// Whether the file at exactly <paramref name="path"/> is another title's: the latest landing Import
    /// recorded there, or the active asset Library has there.
    /// </summary>
    private async Task<bool> FileHeldByAnotherWorkAsync(string path, ImportJob job, CancellationToken cancellationToken)
    {
        var owner = OwnerOf(job);
        var landed = await dbContext.Matches
            .AsNoTracking()
            .Where(m => m.TargetPath == path
                && (m.State == ImportFileMatchState.Operated
                    || m.State == ImportFileMatchState.Probed
                    || m.State == ImportFileMatchState.Registered))
            .Join(dbContext.Jobs, m => m.ImportJobId, j => j.Id, (m, j) => new { j.Id, j.WorkId, j.CreatedAt })
            .OrderByDescending(j => j.CreatedAt)
            .Select(j => j.WorkId == Guid.Empty ? j.Id : j.WorkId)
            .FirstOrDefaultAsync(cancellationToken);
        if (landed != Guid.Empty && landed != owner)
        {
            return true;
        }

        return await library.FindActiveByPathAsync(path, cancellationToken) is { } occupant && occupant.WorkId != owner;
    }

    /// <summary>
    /// The recycle path for a superseded file, never one that is already taken: the bin keeps every copy
    /// it is given. Stamped from the job, not the clock, so a re-drive composes the same candidates in
    /// the same order.
    /// </summary>
    private string? FreeRecyclePath(string existingPath, DateTimeOffset stamp)
    {
        for (var copy = 1; copy <= MaxRecycleCopies; copy++)
        {
            var candidate = organizer.BuildRecyclePath(existingPath, stamp, copy);
            if (candidate is null || !fileSystem.FileExists(candidate))
            {
                return candidate;
            }
        }

        return null;
    }

    /// <summary>How many same-named copies one job may set aside at one instant before it refuses instead.</summary>
    private const int MaxRecycleCopies = 100;

    /// <summary>
    /// Moves whatever already sits at a landing's target path into the recycle folder, as a recorded,
    /// recoverable operation of its own. True when the path is clear afterwards — including the ordinary
    /// case of there having been nothing there at all.
    /// <para>
    /// A file this very job already landed is left alone: that is a re-drive finding its own work, and
    /// recycling it would file a perfectly good import in the bin — on top of the copy it replaced, which
    /// is how a relaunched import used to destroy the household's previous file. That holds whether the
    /// landing was recorded or was interrupted before its row was written. A file another title owns is
    /// never recycled either: it is not a copy of this one.
    /// </para>
    /// </summary>
    private async Task<bool> TryRecycleExistingAsync(
        ImportJob job,
        PlannedLanding landing,
        CancellationToken cancellationToken)
    {
        var landedByThisJob = job.Matches.Any(m =>
            m.IsLanded && m.TargetPath is not null && TargetPathComparer.Equals(m.TargetPath, landing.TargetPath));

        string? recyclePath;
        try
        {
            // This landing's own earlier work — interrupted before its row was written — is the same file
            // or the same bytes as the source, compared whole: a matching size and prefix is not proof.
            if (!fileSystem.FileExists(landing.TargetPath)
                || landedByThisJob
                || await fileSystem.HasSameContentAsync(landing.File.Path, landing.TargetPath, cancellationToken))
            {
                return true;
            }

            if (await FileHeldByAnotherWorkAsync(landing.TargetPath, job, cancellationToken))
            {
                logger.LogWarning(
                    "Import job {JobId} refused to replace a file that belongs to another title.", job.Id);
                return false;
            }

            recyclePath = FreeRecyclePath(landing.TargetPath, job.CreatedAt);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The file there could not be read: refuse this one landing rather than guess, and let the
            // rest of the download go on.
            logger.LogWarning(ex, "Import job {JobId} could not inspect the file at its library path.", job.Id);
            return false;
        }

        if (recyclePath is null)
        {
            logger.LogWarning(
                "Import job {JobId} could not compose a recycle path for {Target}; refusing to overwrite it.",
                job.Id,
                landing.TargetPath);
            return false;
        }

        var operation = job.PlanOperation(landing.Match, FileOperationType.Recycle, recyclePath);
        try
        {
            operation.MarkExecuting();
            await fileSystem.MoveAsync(landing.TargetPath, recyclePath, cancellationToken);

            if (fileSystem.FileExists(landing.TargetPath))
            {
                operation.MarkFailed();
                return false;
            }

            operation.MarkVerified();
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Refuse rather than let the landing delete it: a file the household owns is not something
            // to lose because the bin was unwritable.
            logger.LogWarning(ex, "Import job {JobId} could not recycle {Target}.", job.Id, landing.TargetPath);
            operation.MarkFailed();
            return false;
        }
    }

    /// <summary>Runs every planned operation and returns how many of them failed to verify.</summary>
    private async Task<int> OperateAsync(
        ImportJob job,
        IReadOnlyList<PlannedLanding> landings,
        CancellationToken cancellationToken)
    {
        var failures = 0;
        var copied = false;
        var unidentified = false;
        foreach (var landing in landings)
        {
            // Trash before replace. An episode's library path is derived from its numbering, so an
            // upgrade lands on exactly the path the copy being replaced occupies — and the landing
            // itself would delete it to make room. Setting it aside first is what makes the previous
            // copy recoverable; doing it after would be doing it to a file that no longer exists.
            if (!await TryRecycleExistingAsync(job, landing, cancellationToken))
            {
                failures++;
                landing.Match.MarkFailed($"could not set the existing file aside: {landing.TargetPath}");
                continue;
            }

            // Planned as the intent; the adapter reports what it managed to do and the operation is
            // corrected to that before anything is persisted.
            var operation = job.PlanOperation(landing.Match, FileOperationType.Hardlink, landing.TargetPath);
            var landed = await TryOperateAsync(operation, landing.File, landing.TargetPath, cancellationToken);
            copied |= operation.Type is FileOperationType.Copy;
            // Only for a file that stayed: a landing that failed to verify was rolled back, so there is
            // no file in the library whose provenance the report would be about.
            unidentified |= landed && operation.Type is FileOperationType.Unknown;

            if (landed)
            {
                landing.Match.MarkOperated(landing.TargetPath);
                continue;
            }

            failures++;
            landing.Match.MarkFailed($"file operation failed to verify: {landing.TargetPath}");
        }

        if (copied)
        {
            WarnFilesWereCopied(job);
        }

        if (unidentified)
        {
            WarnLandingCouldNotBeIdentified(job);
        }

        return failures;
    }

    /// <summary>
    /// Says once, per job, that the library got a copy rather than a hardlink. Once and not per file: a
    /// season pack would otherwise repeat one installation-wide fact ten times over. The two
    /// configuration keys are named because they are what an operator has to change; no path is logged,
    /// because a staging path is private layout and a library path spells out the title.
    /// </summary>
    private void WarnFilesWereCopied(ImportJob job) =>
        logger.LogWarning(
            "Import job {JobId} copied at least one file into the library instead of hardlinking it, so "
            + "the library now holds a second full copy of bytes the download is still seeding. The "
            + "download staging root (configuration 'Downloads:Sidecar:StagingPath') and the library root "
            + "(configuration 'Import:LibraryRoot') have to be one filesystem, reachable by the account "
            + "this process runs as; on this installation they are not.",
            job.Id);

    /// <summary>
    /// Says once, per job, that a file was found already in place and the platform would not say what put
    /// it there. Only a resumed job reaches this: an earlier attempt landed the file and was interrupted
    /// before it wrote its row, so the operation type is read back off the two files themselves — and the
    /// filesystem is entitled to refuse that identity.
    /// <para>
    /// It is a warning rather than a note because the persisted <c>Unknown</c> is otherwise one row of one
    /// job that nothing surfaces, and the question it leaves open is one an operator has to answer before
    /// removing a download: whether that frees the space or destroys the household's only copy. The two
    /// paths are left out for the same reason the copy report leaves them out.
    /// </para>
    /// </summary>
    private void WarnLandingCouldNotBeIdentified(ImportJob job) =>
        logger.LogWarning(
            "Import job {JobId} found at least one file already in the library from an earlier attempt "
            + "that was interrupted before it could record what it did, and this filesystem would not say "
            + "whether that file shares its bytes with the download or is a second copy of them. The "
            + "operation is recorded as Unknown rather than guessed: nothing was re-linked and nothing "
            + "was overwritten, but whether removing the download frees that space or leaves the library "
            + "holding the only copy is not something this installation can answer for those files.",
            job.Id);

    /// <summary>
    /// One <c>MediaAvailable</c> per file <paramref name="announced"/> by this drive, then exactly
    /// one <c>ImportCompleted</c> for the whole job.
    /// </summary>
    private void AddRegistrationEvents(
        ImportJob job,
        Guid downloadTaskId,
        IReadOnlyList<ImportFileMatch> announced,
        List<IDomainEvent> events)
    {
        AddMediaAvailableEvents(job, downloadTaskId, announced, events);

        var registered = job.Matches.Where(m => m.State == ImportFileMatchState.Registered).ToList();
        if (registered.Count == 0)
        {
            // Unreachable through the drive above (a job only registers once at least one file
            // landed), but a silent no-event import is the worst possible failure mode here.
            logger.LogError("Import job {JobId} registered with no file; no MediaAvailable was published.", job.Id);
            return;
        }

        var allUnits = registered.SelectMany(m => m.UnitIds).Distinct().ToList();
        events.Add(new ImportCompleted(
            job.Id, job.IntentId, job.AttemptId, registered[0].AssetId, Nullable(allUnits)));
    }

    /// <summary>
    /// One <c>MediaAvailable</c> per file this drive registered — never for one an earlier drive
    /// already announced, since the outbox is at-least-once and would deliver it twice.
    /// </summary>
    private void AddMediaAvailableEvents(
        ImportJob job,
        Guid downloadTaskId,
        IReadOnlyList<ImportFileMatch> announced,
        List<IDomainEvent> events)
    {
        IReadOnlyList<Guid> targetIds = job.TargetId == Guid.Empty ? [] : [job.TargetId];
        foreach (var match in announced)
        {
            events.Add(new MediaAvailable(
                match.AssetId,
                job.WorkId,
                targetIds,
                job.Id,
                downloadTaskId,
                match.TargetPath!,
                match.Size,
                match.MediaInfo ?? MediaInfo.Empty,
                UnitIds: Nullable(match.UnitIds),
                SeasonNumber: match.SeasonNumber,
                EpisodeNumbers: Nullable(match.EpisodeNumbers),
                // Read from the source name, which only exists at this point: the file has already been
                // organised, and a series episode's target name no longer says where it came from.
                Quality: qualityReader.Read(match.SourcePath, job.SourcePath)));
        }
    }

    /// <summary>
    /// The catalog units a movie's landed content serves. The acquisition chain supplies them; when it
    /// does not (an in-flight 1.x row, or a manual import) the job's work id is the unit, which is
    /// exactly the movie case.
    /// </summary>
    private static ResolvedUnits MovieUnits(ImportJob job, IReadOnlyList<Guid>? unitIds)
    {
        if (unitIds is { Count: > 0 })
        {
            return new ResolvedUnits(unitIds, null, [], null, null);
        }

        return job.WorkId == Guid.Empty ? ResolvedUnits.None("no work correlation") : ResolvedUnits.Work(job.WorkId);
    }

    private static EpisodeNaming NamingOf(WorkSummary work, ResolvedUnits resolution) => new(
        work.Title,
        work.Year,
        resolution.SeasonNumber ?? EpisodeFileParser.SpecialsSeasonNumber,
        resolution.EpisodeNumbers,
        resolution.EpisodeTitle);

    /// <summary>Keeps an empty collection off the wire, so a pre-series consumer sees the shape it knows.</summary>
    private static IReadOnlyList<T>? Nullable<T>(IReadOnlyList<T> values) => values.Count == 0 ? null : values;

    private static string? FirstReason(ImportJob job) =>
        job.Matches.Select(m => m.Reason).FirstOrDefault(r => !string.IsNullOrEmpty(r));

    private async Task<ImportJob> OpenJobAsync(
        Guid downloadTaskId,
        Guid intentId,
        Guid attemptId,
        Guid workId,
        Guid targetId,
        string contentPath,
        IReadOnlyList<Guid>? unitIds,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var job = ImportJob.Create(
            downloadTaskId, intentId, attemptId, workId, targetId, contentPath, now, unitIds);
        await unitOfWork.ExecuteAsync(async token =>
        {
            dbContext.Jobs.Add(job); // new root → cascade inserts the genesis history line
            await dbContext.SaveChangesAsync(token);
            await eventBus.PublishAsync(new ImportRequested(job.Id, downloadTaskId), token);
        }, cancellationToken);
        return job;
    }

    /// <summary>
    /// Runs the planned operation against the filesystem and verifies it landed intact. Returns false
    /// (and rolls back a partial result) on any recoverable I/O failure, leaving the operation marked
    /// so the trail explains what happened.
    /// </summary>
    private async Task<bool> TryOperateAsync(
        FileOperation operation,
        ImportFileEntry source,
        string targetPath,
        CancellationToken cancellationToken)
    {
        // A file already there is this landing's own earlier work (the recycle step made sure of that):
        // a failure now must leave it, not delete it. Only what this operation created is rolled back.
        var existedBefore = fileSystem.FileExists(targetPath);
        try
        {
            operation.MarkExecuting();
            var directory = Path.GetDirectoryName(targetPath);
            if (!string.IsNullOrEmpty(directory))
            {
                await fileSystem.EnsureDirectoryAsync(directory, cancellationToken);
            }

            // Recorded from the outcome, not from the plan: HardlinkOrCopyAsync falls back to a copy when
            // the platform refuses the link, and the trail has to say which of the two happened.
            operation.RecordPerformed(
                await fileSystem.HardlinkOrCopyAsync(source.Path, targetPath, cancellationToken));

            if (!fileSystem.FileExists(targetPath) || fileSystem.GetSize(targetPath) != source.Size)
            {
                await RollbackAsync(operation, targetPath, existedBefore, cancellationToken);
                return false;
            }

            operation.MarkVerified();
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(ex, "Import file operation failed for {Target}; rolling back.", targetPath);
            await RollbackAsync(operation, targetPath, existedBefore, cancellationToken);
            return false;
        }
    }

    private async Task RollbackAsync(
        FileOperation operation, string targetPath, bool existedBefore, CancellationToken cancellationToken)
    {
        operation.MarkFailed();
        if (existedBefore)
        {
            // Not this operation's to remove: it found the file there.
            operation.MarkRolledBack();
            return;
        }

        try
        {
            await fileSystem.DeleteAsync(targetPath, cancellationToken);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogError(ex, "Rollback could not remove partial file {Target}; manual attention needed.", targetPath);
        }

        operation.MarkRolledBack();
    }

    private async Task PersistDriveAsync(
        ImportJob job,
        ImportDrive drive,
        IReadOnlyList<IDomainEvent> events,
        CancellationToken cancellationToken)
    {
        var newMatches = job.Matches.Skip(drive.MatchesBefore).ToList();
        var newHistory = job.History.Skip(drive.HistoryBefore).ToList();
        var newOperations = job.Operations.Skip(drive.OperationsBefore).ToList();
        await unitOfWork.ExecuteAsync(async token =>
        {
            // Children reached through the tracked aggregate's navigations carry client-assigned keys,
            // so EF would mis-track them as Modified — add them to their sets explicitly.
            dbContext.Matches.AddRange(newMatches);
            dbContext.History.AddRange(newHistory);
            dbContext.Operations.AddRange(newOperations);
            await dbContext.SaveChangesAsync(token);
            foreach (var domainEvent in events)
            {
                await eventBus.PublishAsync(domainEvent, token);
            }
        }, cancellationToken);
    }

    private Task<ImportJob?> LoadByDownloadAsync(Guid downloadTaskId, CancellationToken cancellationToken) =>
        dbContext.Jobs
            .Include(j => j.Matches)
            .Include(j => j.Operations)
            .Include(j => j.History)
            .FirstOrDefaultAsync(j => j.DownloadTaskId == downloadTaskId, cancellationToken);

    /// <summary>One file that resolved to a unit and to a confined path, ready to be hardlinked.</summary>
    private sealed record PlannedLanding(ImportFileMatch Match, ImportFileEntry File, string TargetPath);

    /// <summary>
    /// How many children the job already held when this drive started, so only the ones this drive
    /// appends are added to their EF sets (the rest are already tracked as loaded rows).
    /// </summary>
    private readonly record struct ImportDrive(int MatchesBefore, int OperationsBefore, int HistoryBefore)
    {
        public static ImportDrive Open(ImportJob job) =>
            new(job.Matches.Count, job.Operations.Count, job.History.Count);
    }
}
