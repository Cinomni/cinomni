using Cinomni.Kernel.Identifiers;
using Cinomni.Library.Contracts;
using Cinomni.Operations.Messaging;
using Cinomni.Operations.Persistence;
using Cinomni.Subtitles.Application;
using Cinomni.Subtitles.Contracts;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Cinomni.Subtitles.Tests;

/// <summary>
/// Where the provider stagger is paid. The platform runs ONE command worker and
/// <c>CommandProcessor.ProcessBatchAsync</c> dispatches its batch strictly sequentially, so a season
/// pack's N <c>search-subtitles</c> commands must not each hold that worker for an interval — every
/// other module's commands queue behind them. The stagger belongs on the schedule of the outbound
/// calls, not on the loop that drives the whole monolith.
/// <para>
/// Time here is the <see cref="VirtualClock"/> the test owns, not the runner's. "The worker did not
/// wait" is therefore the exact statement <c>Charged == 0</c>, not "it finished within N ms", which on
/// a contended machine measures the test's own database round trips as much as the thing under test.
/// The same clock drives the command queue, so a command deferred to a later slot is provably not
/// picked up before the test moves the world to that slot.
/// </para>
/// <para>
/// Its own database: every TestHost opens with <c>EnsureDeletedAsync</c> and xUnit parallelises test
/// classes, so sharing a name with another suite drops its database mid-run.
/// </para>
/// </summary>
public sealed class SubtitleThrottleTests : IAsyncLifetime
{
    private const string Database = "cinomni_test_subtitles_throttle";
    private const int AssetCount = 4;

    /// <summary>Long enough that holding the worker for it would be unmistakable on the clock below.</summary>
    private static readonly TimeSpan Interval = TimeSpan.FromMilliseconds(500);

    private readonly VirtualClock _clock = new(new DateTimeOffset(2026, 3, 1, 12, 0, 0, TimeSpan.Zero));
    private readonly FakeSubtitleProvider _provider = new();
    private readonly FakeSubtitleFileStore _fileStore = new();
    private ServiceProvider _host = null!;

    public async Task InitializeAsync() =>
        _host = await SubtitlesTestHost.CreateAsync(
            Database,
            _provider,
            _fileStore,
            new SubtitleOptions { WantedLanguages = ["en"], MinScore = 5, ProviderCallInterval = Interval },
            services => services.AddSingleton<TimeProvider>(_clock));

    public async Task DisposeAsync() => await _host.DisposeAsync();

    [Fact]
    public async Task A_burst_of_registrations_does_not_hold_the_command_worker()
    {
        // Arrange — a season pack lands N assets, each of which enqueues its own search command.
        for (var i = 1; i <= AssetCount; i++)
        {
            await RegisterAssetAsync(i);
        }

        await DrainOutboxAsync();

        // Act — one pass of the shared command worker.
        var processed = await ProcessCommandBatchAsync();

        // Assert — the worker took one search and moved on instead of sleeping through the burst.
        Assert.Equal(1, processed);
        Assert.True(
            _clock.Charged == TimeSpan.Zero,
            $"the command batch waited out {_clock.Charged.TotalMilliseconds} ms of stagger on the shared worker.");

        // ...because the burst was spread over the wall clock: the provider still cannot be hit faster
        // than the configured interval.
        var schedule = await SearchScheduleAsync();
        Assert.Equal(AssetCount, schedule.Count);
        for (var i = 1; i < schedule.Count; i++)
        {
            Assert.True(
                schedule[i] - schedule[i - 1] >= Interval,
                $"searches {i - 1} and {i} are scheduled {(schedule[i] - schedule[i - 1]).TotalMilliseconds} ms apart.");
        }

        // ...and none of the deferred searches is lost: each one runs when its turn comes, and not one
        // slot before it.
        for (var i = 1; i < AssetCount; i++)
        {
            Assert.Equal(0, await ProcessCommandBatchAsync());
            _clock.Advance(Interval);
            Assert.Equal(1, await ProcessCommandBatchAsync());
        }

        Assert.Equal(AssetCount, _provider.Searches.Count);

        // ...and the gate never charged anyone: by the time a scheduled search runs, its wait is spent.
        Assert.True(
            _clock.Charged == TimeSpan.Zero,
            $"the scheduled searches still paid {_clock.Charged.TotalMilliseconds} ms at the gate.");
    }

    [Fact]
    public async Task An_unconfigured_provider_is_never_consulted_and_costs_no_stagger()
    {
        // Arrange — the default install: the adapters are registered but no API key is configured.
        _provider.IsConfigured = false;
        var assetIds = new List<Guid>();
        for (var i = 1; i <= 3; i++)
        {
            assetIds.Add(await RegisterAssetAsync(i));
        }

        // Act — search each asset in turn, the way the command worker would.
        foreach (var assetId in assetIds)
        {
            await SearchAsync(assetId);
        }

        // Assert — no provider was asked, so no interval was charged for one.
        Assert.Empty(_provider.Searches);
        Assert.True(
            _clock.Charged == TimeSpan.Zero,
            $"three searches against an unconfigured provider were charged {_clock.Charged.TotalMilliseconds} ms of stagger.");
    }

    // -- helpers ---------------------------------------------------------------------------------

    private async Task<Guid> RegisterAssetAsync(int index)
    {
        var assetId = Uuid7.New();
        var request = new RegisterMediaAssetRequest(
            assetId,
            Uuid7.New(),
            TargetIds: [Uuid7.New()],
            FullPath: $"/data/library/Show (2020)/Season 01/Show - S01E{index:00}.mkv",
            Size: 2_000_000_000,
            Container: "matroska",
            Streams:
            [
                new MediaStreamInput(0, MediaStreamType.Video, "h264", null, null, 1920, 1080, null, null, true, false),
                new MediaStreamInput(1, MediaStreamType.Audio, "aac", "eng", 6, null, null, null, null, true, false),
            ]);

        await using var scope = _host.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<ILibraryCommands>().RegisterMediaAssetAsync(request);
        return assetId;
    }

    private async Task SearchAsync(Guid assetId)
    {
        await using var scope = _host.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<ISubtitleSearch>().SearchForAssetAsync(assetId);
    }

    /// <summary>When each queued subtitle search is allowed to run, oldest first.</summary>
    private async Task<IReadOnlyList<DateTimeOffset>> SearchScheduleAsync()
    {
        await using var scope = _host.CreateAsyncScope();
        var operationsDb = scope.ServiceProvider.GetRequiredService<OperationsDbContext>();
        var commands = await operationsDb.Commands
            .Where(c => c.IdempotencyKey.StartsWith("search-subtitles:"))
            .OrderBy(c => c.QueuedAt)
            .Select(c => new { c.QueuedAt, c.RunAfter })
            .ToListAsync();

        return [.. commands.Select(c => c.RunAfter ?? c.QueuedAt)];
    }

    private async Task<int> ProcessCommandBatchAsync()
    {
        await using var scope = _host.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<CommandProcessor>().ProcessBatchAsync();
    }

    private async Task DrainOutboxAsync()
    {
        await using var scope = _host.CreateAsyncScope();
        var relay = scope.ServiceProvider.GetRequiredService<OutboxRelay>();
        while (await relay.ProcessBatchAsync() > 0)
        {
        }
    }
}
