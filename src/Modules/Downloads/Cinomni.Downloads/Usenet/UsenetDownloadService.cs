using System.Text.Json;
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

    private static readonly TimeSpan TickBudget = TimeSpan.FromSeconds(20);
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = false };
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

        Directory.CreateDirectory(task.SavePath);
        NzbDocument document;
        try
        {
            document = NzbDocumentParser.Parse(await ReadNzbAsync(task, cancellationToken));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "A Usenet release could not be read.");
            await FailAsync(task, "The NZB could not be read.", cancellationToken);
            return;
        }

        if (NzbPolicy.Refusal(document) is { } refusal)
        {
            await FailAsync(task, refusal, cancellationToken);
            return;
        }

        if (!options.IsConfigured)
        {
            await FailAsync(task, NoServer, cancellationToken);
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
            await CompleteAsync(task, document, cancellationToken);
            return;
        }

        var now = DateTimeOffset.UtcNow;
        var events = new List<IDomainEvent>();
        if (task.MarkDownloading(now))
        {
            events.AddRange(Claims(task, c => new DownloadStarted(task.Id, c.IntentId, c.AttemptId, string.Empty)));
        }

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
                await File.WriteAllTextAsync(ProgressPath(task.SavePath), JsonSerializer.Serialize(done, Json), cancellationToken);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "A Usenet article could not be fetched.");
            await FailAsync(task, "The Usenet server could not supply an article.", cancellationToken);
            return;
        }

        if (document.Files.Where(file => NzbPolicy.IsVideo(file.Name)).All(file => file.Segments.All(segment => done.Contains(segment.MessageId))))
        {
            await CompleteAsync(task, document, cancellationToken);
            return;
        }

        await PersistAsync(task, events, cancellationToken);
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

    private async Task CompleteAsync(DownloadTask task, NzbDocument document, CancellationToken cancellationToken)
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

        if (task.MarkCompleted(task.SavePath, now))
        {
            DownloadsMetrics.RecordFinished(string.Empty, CinomniTelemetry.Outcomes.Completed);
            var units = task.Units.Count > 0 ? task.Units.Select(u => u.UnitId).ToList() : new List<Guid> { task.WorkId };
            var files = task.Files.OrderBy(f => f.Index).Select(f => new DownloadedFile(f.Index, f.Path, f.Size)).ToList();
            events.AddRange(Claims(task, c => new DownloadCompleted(
                task.Id, c.IntentId, c.AttemptId, task.WorkId, c.TargetId, string.Empty, task.SavePath, units, files)));
        }

        await PersistAsync(task, events, cancellationToken);
    }

    private async Task FailAsync(DownloadTask task, string reason, CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        if (!task.Fail(reason, now))
        {
            return;
        }

        DownloadsMetrics.RecordFinished(string.Empty, CinomniTelemetry.Outcomes.Failed);
        await PersistAsync(task, Claims(task, c => new DownloadFailed(task.Id, c.IntentId, c.AttemptId, reason)).ToList(), cancellationToken);
    }

    private async Task PersistAsync(DownloadTask task, IReadOnlyList<IDomainEvent> events, CancellationToken cancellationToken)
    {
        var historyBefore = task.History.Count;
        await unitOfWork.ExecuteAsync(async token =>
        {
            dbContext.History.AddRange(task.History.Skip(historyBefore));
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

        var ids = JsonSerializer.Deserialize<List<string>>(File.ReadAllText(path)) ?? [];
        return ids.ToHashSet(StringComparer.Ordinal);
    }

    private static string ProgressPath(string savePath) => Path.Combine(savePath, ".cinomni-usenet.json");

    private static string PartPath(string savePath, string name, int number)
    {
        var dir = Path.Combine(savePath, ".parts", name);
        Directory.CreateDirectory(dir);
        return Path.Combine(dir, number.ToString(System.Globalization.CultureInfo.InvariantCulture));
    }
}
