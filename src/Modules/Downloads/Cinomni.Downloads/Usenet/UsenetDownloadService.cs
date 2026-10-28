using System.Globalization;
using Cinomni.Discovery.Contracts;
using Cinomni.Downloads.Application;
using Cinomni.Downloads.Contracts;
using Cinomni.Downloads.Diagnostics;
using Cinomni.Downloads.Engine;
using Cinomni.Downloads.Persistence;
using Cinomni.Kernel.Diagnostics;
using Cinomni.Kernel.Messaging;
using Cinomni.Operations.Messaging;
using Cinomni.Operations.Transactions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Cinomni.Downloads.Usenet;

/// <summary>
/// Advances one Usenet download a short way, then returns. The torrent engine is never asked.
/// A packed NZB is refused before any article is fetched. Progress is the parts already on disk,
/// so a tick that is run again does not fetch them twice.
/// <para>
/// A server that cannot be reached is waited out for <see cref="OutageGrace"/>, then the download
/// fails. An article it does not have fails the download at once: repair is not built, so waiting
/// would not bring it back. Once the download is settled, its working files are removed.
/// </para>
/// </summary>
public sealed class UsenetDownloadService(
    DownloadsDbContext dbContext,
    IUnitOfWork unitOfWork,
    IEventBus eventBus,
    UsenetOptions options,
    IHttpClientFactory httpClientFactory,
    ILogger<UsenetDownloadService> logger,
    IReleaseFileSource? releaseFiles = null)
{
    public const string Protocol = "Usenet";
    public const string NoServer = "No Usenet server is configured.";
    public const string ClientName = "usenet-nzb";
    internal const string OutageMarker = ".cinomni-usenet-down";

    /// <summary>
    /// The ids of the parts stored so far, one to a line, added as each part lands. Rewriting the whole
    /// record per part would cost writes that grow with the square of the release, and a release of
    /// 10 GB is thousands of parts. A line is also what lets a record cut short be told from a whole one.
    /// </summary>
    internal const string ProgressFile = ".cinomni-usenet-done";
    private const string PartsFolder = ".parts";

    private static readonly TimeSpan TickBudget = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan OutageGrace = TimeSpan.FromMinutes(15);
    private static readonly SemaphoreSlim Gate = new(1, 1);

    public async Task AdvanceAsync(CancellationToken cancellationToken)
    {
        if (!await Gate.WaitAsync(0, cancellationToken))
        {
            return;
        }

        try
        {
            var id = await dbContext.Tasks
                .AsNoTracking()
                .Where(t => t.Protocol == Protocol && (t.State == DownloadState.Queued || t.State == DownloadState.Downloading))
                .OrderBy(t => t.CreatedAt)
                .Select(t => t.Id)
                .FirstOrDefaultAsync(cancellationToken);
            if (id == Guid.Empty)
            {
                return;
            }

            await AdvanceOneAsync(id, cancellationToken);
        }
        finally
        {
            Gate.Release();
        }
    }

    private async Task AdvanceOneAsync(Guid id, CancellationToken cancellationToken)
    {
        var task = await dbContext.Tasks
            .Include(t => t.History)
            .Include(t => t.Claims)
            .Include(t => t.Files)
            .Include(t => t.Units)
            .FirstOrDefaultAsync(t => t.Id == id, cancellationToken);
        if (task is null || task.Protocol != Protocol
            || task.State is not (DownloadState.Queued or DownloadState.Downloading))
        {
            return;
        }

        // Before anything below changes the task: what the history gained is what this tick adds to it.
        var historyBefore = task.History.Count;
        Directory.CreateDirectory(task.SavePath);
        NzbDocument document;
        try
        {
            document = NzbDocumentParser.Parse(await ReadNzbAsync(task, cancellationToken));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "A Usenet release could not be read.");
            await FailAsync(task, historyBefore, "The NZB could not be read.", cancellationToken);
            return;
        }

        if (NzbPolicy.Refusal(document) is { } refusal)
        {
            await FailAsync(task, historyBefore, refusal, cancellationToken);
            return;
        }

        if (!options.IsConfigured)
        {
            await FailAsync(task, historyBefore, NoServer, cancellationToken);
            return;
        }

        var done = ReadProgress(task.SavePath);
        var pending = document.Files
            .Where(file => NzbPolicy.IsVideo(file.Name))
            .SelectMany(file => file.Segments.Select(segment => (file, segment)))
            .Where(item => !done.Contains(item.segment.MessageId))
            .ToList();
        if (pending.Count == 0)
        {
            await CompleteAsync(task, document, historyBefore, cancellationToken);
            return;
        }

        var now = DateTimeOffset.UtcNow;
        var events = new List<IDomainEvent>();
        if (task.MarkDownloading(now))
        {
            events.AddRange(Claims(task, c => new DownloadStarted(task.Id, c.IntentId, c.AttemptId, string.Empty)));
        }

        var stored = 0;
        NntpUnavailableException? outage = null;
        try
        {
            await using var client = await NntpClient.ConnectAsync(options, cancellationToken);
            var deadline = DateTimeOffset.UtcNow + TickBudget;
            foreach (var (file, segment) in pending)
            {
                if (DateTimeOffset.UtcNow >= deadline)
                {
                    break;
                }

                var article = await client.ArticleAsync(segment.MessageId, cancellationToken);
                var decoded = Yenc.Decode(article);
                await File.WriteAllBytesAsync(PartPath(task.SavePath, file.Name, segment.Number), decoded, cancellationToken);
                done.Add(segment.MessageId);
                await File.AppendAllTextAsync(ProgressPath(task.SavePath), segment.MessageId + "\n", cancellationToken);
                stored++;
            }
        }
        catch (NntpUnavailableException ex)
        {
            // The server's bad minute, not an answer about this release. The next tick starts again from
            // the parts on disk.
            outage = ex;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "A Usenet article could not be fetched.");
            await FailAsync(task, historyBefore, "The Usenet server could not supply an article.", cancellationToken);
            return;
        }

        if (stored > 0)
        {
            ClearOutage(task.SavePath);
        }

        if (outage is not null)
        {
            if (stored == 0 && OutageOutlastedGrace(task.SavePath, DateTimeOffset.UtcNow))
            {
                logger.LogWarning(outage, "The Usenet server has not answered for {Grace}.", OutageGrace);
                await FailAsync(task, historyBefore, "The Usenet server did not answer for too long.", cancellationToken);
                return;
            }

            logger.LogWarning("The Usenet server could not be reached ({Reason}); the download will try again.", outage.Message);
        }

        if (document.Files.Where(file => NzbPolicy.IsVideo(file.Name)).All(file => file.Segments.All(segment => done.Contains(segment.MessageId))))
        {
            await CompleteAsync(task, document, historyBefore, cancellationToken);
            return;
        }

        await PersistAsync(task, historyBefore, events, cancellationToken);
    }

    private async Task<string> ReadNzbAsync(DownloadTask task, CancellationToken cancellationToken)
    {
        var path = Path.Combine(task.SavePath, "release.nzb");
        if (File.Exists(path))
        {
            return await File.ReadAllTextAsync(path, cancellationToken);
        }

        byte[] bytes;
        if (releaseFiles is not null)
        {
            var fetched = await releaseFiles.FetchAsync(task.DownloadUrl, cancellationToken);
            if (fetched.Outcome == ReleaseFileOutcome.TorrentFile && fetched.TorrentFile is { Length: > 0 } file)
            {
                bytes = file;
            }
            else
            {
                bytes = await FetchAnonymousAsync(task.DownloadUrl, cancellationToken);
            }
        }
        else
        {
            bytes = await FetchAnonymousAsync(task.DownloadUrl, cancellationToken);
        }

        await File.WriteAllBytesAsync(path, bytes, cancellationToken);
        return System.Text.Encoding.UTF8.GetString(bytes);
    }

    private async Task<byte[]> FetchAnonymousAsync(string url, CancellationToken cancellationToken)
    {
        var http = httpClientFactory.CreateClient(ClientName);
        var fetched = await TorrentLinkFetch.FetchAsync(http, url, logger, cancellationToken);
        return fetched.File ?? throw new InvalidOperationException("The NZB link did not return a file.");
    }

    private async Task CompleteAsync(DownloadTask task, NzbDocument document, int historyBefore, CancellationToken cancellationToken)
    {
        var index = 0;
        var layout = new List<(int Index, string Path, long Size, FilePriorityLevel Priority)>();
        foreach (var file in document.Files.Where(file => NzbPolicy.IsVideo(file.Name)))
        {
            var target = Path.Combine(task.SavePath, file.Name);
            await using (var output = File.Create(target))
            {
                foreach (var segment in file.Segments)
                {
                    await using var part = File.OpenRead(PartPath(task.SavePath, file.Name, segment.Number));
                    await part.CopyToAsync(output, cancellationToken);
                }
            }

            layout.Add((index, file.Name, new FileInfo(target).Length, FilePriorityLevel.Normal));
            index++;
        }

        task.SetFiles(layout);
        var now = DateTimeOffset.UtcNow;
        var events = new List<IDomainEvent>();
        if (task.MarkDownloading(now))
        {
            events.AddRange(Claims(task, c => new DownloadStarted(task.Id, c.IntentId, c.AttemptId, string.Empty)));
        }

        var completed = task.MarkCompleted(task.SavePath, now);
        if (completed)
        {
            DownloadsMetrics.RecordFinished(string.Empty, CinomniTelemetry.Outcomes.Completed);
            var units = task.Units.Count > 0 ? task.Units.Select(u => u.UnitId).ToList() : new List<Guid> { task.WorkId };
            var files = task.Files.OrderBy(f => f.Index).Select(f => new DownloadedFile(f.Index, f.Path, f.Size)).ToList();
            events.AddRange(Claims(task, c => new DownloadCompleted(
                task.Id, c.IntentId, c.AttemptId, task.WorkId, c.TargetId, string.Empty, task.SavePath, units, files)));
        }

        await PersistAsync(task, historyBefore, events, cancellationToken);
        if (completed)
        {
            // Only once the row says it is done: until then a tick that is run again assembles the file
            // from these parts.
            DiscardScratch(task.SavePath);
        }
    }

    private async Task FailAsync(DownloadTask task, int historyBefore, string reason, CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        if (!task.Fail(reason, now))
        {
            return;
        }

        DownloadsMetrics.RecordFinished(string.Empty, CinomniTelemetry.Outcomes.Failed);
        await PersistAsync(task, historyBefore, Claims(task, c => new DownloadFailed(task.Id, c.IntentId, c.AttemptId, reason)).ToList(), cancellationToken);
        // An Error task is never advanced again, and its folder is its own, so what was fetched is not
        // coming back and is as large as the release.
        DiscardScratch(task.SavePath);
    }

    /// <summary>
    /// A server that answers nothing is waited out, but not for ever: the first tick that stored no
    /// article writes the time down, and one that finds it older than <see cref="OutageGrace"/> says so.
    /// Failing frees the acquisition to look for another release.
    /// </summary>
    private static bool OutageOutlastedGrace(string savePath, DateTimeOffset now)
    {
        var path = Path.Combine(savePath, OutageMarker);
        if (File.Exists(path)
            && DateTimeOffset.TryParse(File.ReadAllText(path), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var since))
        {
            return now - since > OutageGrace;
        }

        File.WriteAllText(path, now.ToString("O", CultureInfo.InvariantCulture));
        return false;
    }

    private static void ClearOutage(string savePath) => File.Delete(Path.Combine(savePath, OutageMarker));

    /// <summary>
    /// Removes what a download needed only while it was running: the parts, the record of which were
    /// stored, and the outage marker. What is left beside the file is the NZB.
    /// </summary>
    private void DiscardScratch(string savePath)
    {
        var parts = Path.Combine(savePath, PartsFolder);
        try
        {
            if (Directory.Exists(parts))
            {
                Directory.Delete(parts, recursive: true);
            }

            File.Delete(ProgressPath(savePath));
            File.Delete(Path.Combine(savePath, OutageMarker));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The download is already settled. A folder that will not go is the operator's to remove,
            // and failing a finished download over it would be worse.
            logger.LogWarning(ex, "The working files of a Usenet download could not be removed.");
        }
    }

    private async Task PersistAsync(DownloadTask task, int historyBefore, IReadOnlyList<IDomainEvent> events, CancellationToken cancellationToken)
    {
        var newHistory = task.History.Skip(historyBefore).ToList();
        await unitOfWork.ExecuteAsync(async token =>
        {
            dbContext.History.AddRange(newHistory);
            await dbContext.SaveChangesAsync(token);
            foreach (var domainEvent in events)
            {
                await eventBus.PublishAsync(domainEvent, token);
            }
        }, cancellationToken);
    }

    private static IEnumerable<IDomainEvent> Claims(DownloadTask task, Func<DownloadTaskClaim, IDomainEvent> build)
    {
        if (task.Claims.Count == 0)
        {
            return [build(new DownloadTaskClaim { DownloadTaskId = task.Id, IntentId = task.IntentId, AttemptId = task.AttemptId, TargetId = task.TargetId })];
        }

        return task.Claims.Select(build);
    }

    private static HashSet<string> ReadProgress(string savePath)
    {
        var path = ProgressPath(savePath);
        if (!File.Exists(path))
        {
            return new HashSet<string>(StringComparer.Ordinal);
        }

        // Whatever follows the last line end is a record that was being written when the process stopped.
        // It is an id nobody confirmed the part for, even when it is a whole one, so it is not trusted:
        // the part is fetched again.
        var lines = File.ReadAllText(path).Split('\n');
        return lines[..^1].ToHashSet(StringComparer.Ordinal);
    }

    private static string ProgressPath(string savePath) => Path.Combine(savePath, ProgressFile);

    private static string PartPath(string savePath, string name, int number)
    {
        var dir = Path.Combine(savePath, PartsFolder, name);
        Directory.CreateDirectory(dir);
        return Path.Combine(dir, number.ToString(CultureInfo.InvariantCulture));
    }
}
