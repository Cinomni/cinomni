using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Cinomni.Host.Health;

/// <summary>What was last known about one storage root.</summary>
internal enum StorageSpaceState
{
    /// <summary>Nothing has been read yet. Nothing is known to be wrong either.</summary>
    Unknown,

    /// <summary>The root answered and has room.</summary>
    Ample,

    /// <summary>The root answered and is below its floor.</summary>
    Low,

    /// <summary>The root is not there. A mount is missing.</summary>
    Missing,

    /// <summary>The root is there but its volume could not be interrogated.</summary>
    Unreadable,
}

/// <summary>A root this installation watches, resolved from the options the owning module bound.</summary>
/// <param name="Name">The fixed check name. Never a path — it reaches an anonymous caller.</param>
/// <param name="Path">The directory to watch.</param>
/// <param name="MinimumFreeBytes">Free space below which the root is reported degraded.</param>
internal sealed record StorageRoot(string Name, string Path, long MinimumFreeBytes);

/// <summary>
/// Reads each storage root's free space on a cadence this process owns, so a readiness probe can answer
/// from the last reading instead of from a syscall.
/// <para>
/// <b>Why the request path may not touch a disk.</b> <c>Directory.Exists</c> and
/// <c>DriveInfo.AvailableFreeSpace</c> are blocking syscalls with no deadline, and no
/// <see cref="CancellationToken"/> can interrupt them. A library root on a NAS is a documented topology,
/// and a stale NFS or SMB mount makes both of them block indefinitely. If the probe called them, every
/// poll would strand a thread-pool thread for ever, and <c>/health</c> — anonymous, and polled by the
/// container every thirty seconds — would stop answering. The probe that exists to reveal trouble would
/// then be the thing that takes the process down, which is strictly worse than not probing storage at
/// all. So the reading is taken here, off the request path, exactly as
/// <c>QueueDepthSampler</c> keeps a database query off an exporter's collection callback.
/// </para>
/// <para>
/// <b>A wedged root ages out rather than piling up.</b> A measurement that has not come back blocks no
/// later pass: the next tick skips that root, its reading goes stale, and a stale reading reports
/// degraded. A root that cannot answer within <see cref="StaleAfter"/> is not a root that is known to be
/// fine, and saying so is the whole point — while the API keeps serving.
/// </para>
/// </summary>
internal sealed class StorageSpaceSampler
{
    /// <summary>
    /// How often a root is read. Slower than any probe interval on purpose: free space is a level that
    /// moves in minutes, and reading it faster would only add syscalls.
    /// </summary>
    public static readonly TimeSpan SampleInterval = TimeSpan.FromSeconds(15);

    /// <summary>
    /// How long a reading stays trustworthy. Four sample intervals, so a slow but working mount does not
    /// flap, and a mount that has stopped answering shows up within a minute.
    /// </summary>
    public static readonly TimeSpan StaleAfter = TimeSpan.FromMinutes(1);

    private readonly Dictionary<string, WatchedRoot> _roots;

    /// <param name="roots">The roots to watch. A composition without the owning module simply has fewer.</param>
    /// <param name="startedAt">
    /// When watching began. It is the age of the first reading, so a root that never answers at all is
    /// reported stale rather than healthy for ever.
    /// </param>
    public StorageSpaceSampler(IEnumerable<StorageRoot> roots, DateTimeOffset startedAt) =>
        _roots = roots.ToDictionary(root => root.Name, root => new WatchedRoot(root, startedAt), StringComparer.Ordinal);

    /// <summary>
    /// Answers for one root from the last reading. No I/O, no allocation of a syscall, no way to block.
    /// </summary>
    /// <param name="name">A check name from <see cref="HealthRegistration"/>.</param>
    /// <param name="now">The moment the answer is given, against which the reading is aged.</param>
    public HealthCheckResult Evaluate(string name, DateTimeOffset now)
    {
        if (!_roots.TryGetValue(name, out var root))
        {
            // The module that owns this root is not part of this composition — an installation
            // registered without the production import or playback adapters has no such root, and a
            // probe must report nothing rather than invent a failure.
            return HealthCheckResult.Healthy();
        }

        var reading = root.Reading;

        // Degraded, not unhealthy: the root may well be fine and simply slow, and taking the node out of
        // rotation for a reading we do not have would turn a storage question into a total outage.
        if (now - reading.TakenAt > StaleAfter)
        {
            return HealthCheckResult.Degraded();
        }

        return reading.State switch
        {
            // A vanished root means a mount is missing, which is a different failure from a full one.
            StorageSpaceState.Missing => HealthCheckResult.Unhealthy(),

            // A full disk stops new imports; it does not stop the library, playback of what is already
            // there, or the interface an operator needs in order to fix it.
            StorageSpaceState.Low or StorageSpaceState.Unreadable => HealthCheckResult.Degraded(),

            // Unknown included: nothing has been read yet and nothing is known to be wrong. It cannot
            // stay unknown, because an unread root ages past StaleAfter.
            _ => HealthCheckResult.Healthy(),
        };
    }

    /// <summary>
    /// Starts one measurement per idle root and completes when they do. Callable directly so a test needs
    /// no timer.
    /// </summary>
    /// <param name="now">Stamped on every reading this pass produces.</param>
    public Task RefreshAsync(DateTimeOffset now) =>
        Task.WhenAll(_roots.Values.Select(root => root.RefreshAsync(now)));

    /// <summary>The configured path is never in a reading, only the state — the answer reaches anyone.</summary>
    private sealed record Reading(StorageSpaceState State, DateTimeOffset TakenAt);

    private sealed class WatchedRoot(StorageRoot root, DateTimeOffset startedAt)
    {
        private int _measuring;
        private Reading _reading = new(StorageSpaceState.Unknown, startedAt);

        public Reading Reading => Volatile.Read(ref _reading);

        public Task RefreshAsync(DateTimeOffset now)
        {
            if (Interlocked.CompareExchange(ref _measuring, 1, 0) != 0)
            {
                // The previous measurement has not come back — the stale-mount case this class exists
                // for. Queueing a second thread behind it would leak one per tick; letting the reading
                // age instead is what turns a wedged root into a signal.
                return Task.CompletedTask;
            }

            // Deliberately on a pool thread: the measurement is a blocking syscall with no deadline, so
            // it may not run on the loop that owns the cadence any more than on the request path.
            return Task.Run(() =>
            {
                try
                {
                    Volatile.Write(ref _reading, new Reading(Measure(), now));
                }
                finally
                {
                    Volatile.Write(ref _measuring, 0);
                }
            });
        }

        private StorageSpaceState Measure()
        {
            try
            {
                if (!Directory.Exists(root.Path))
                {
                    return StorageSpaceState.Missing;
                }

                var free = new DriveInfo(Path.GetPathRoot(Path.GetFullPath(root.Path)) ?? root.Path)
                    .AvailableFreeSpace;

                return free >= root.MinimumFreeBytes ? StorageSpaceState.Ample : StorageSpaceState.Low;
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                // An unusual mount, a permission problem, a volume that refuses to answer. Reported as
                // unreadable rather than missing: nothing is known to be broken, and a sampler must
                // never be able to fault the loop that drives it.
                return StorageSpaceState.Unreadable;
            }
        }
    }
}

/// <summary>Drives <see cref="StorageSpaceSampler"/> on a fixed tick.</summary>
internal sealed class StorageSpaceSamplerHostedService(StorageSpaceSampler sampler) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            // Deliberately not awaited. A measurement that never returns — a stale network mount — must
            // not stop the cadence; the sampler already refuses to start a second measurement for a root
            // that is still blocked, and the reading ages into a degraded answer by itself.
            _ = sampler.RefreshAsync(DateTimeOffset.UtcNow);

            try
            {
                await Task.Delay(StorageSpaceSampler.SampleInterval, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
        }
    }
}
