using System.Collections.Concurrent;
using Cinomni.Monitoring.Contracts;
using Cinomni.Discovery.Contracts;
using Cinomni.Decision.Application;
using Cinomni.Decision.Contracts;
using Cinomni.Decision.Persistence;
using Cinomni.Discovery.Persistence;
using Cinomni.Kernel.Identifiers;
using Cinomni.Kernel.Messaging;
using Cinomni.Operations.Messaging;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Cinomni.Decision.Tests;

/// <summary>
/// A block is an operator decision, not a profile verdict. The sweep must leave that release alone
/// even when the profile would have taken it, and the reason has to outlive the evaluation.
/// </summary>
public sealed class ReleaseBlocklistTests : IAsyncLifetime
{
    private readonly EventSink _sink = new();
    private ServiceProvider _provider = null!;

    public async Task InitializeAsync() =>
        _provider = await DecisionTestHost.CreateAsync("cinomni_test_decision_blocklist", services =>
        {
            services.AddSingleton(_sink);
            services.AddSingleton<ITargetSearchPlans, UnusedPlans>();
            services.AddScoped<IEventHandler<ReleaseSelected>, ReleaseSelectedSink>();
            services.AddScoped<IEventHandler<NoAcceptableRelease>, NoAcceptableSink>();
        });

    public async Task DisposeAsync() => await _provider.DisposeAsync();

    [Fact]
    public async Task The_sweep_skips_a_blocked_release_and_records_why()
    {
        var searchId = Uuid7.New();
        var targetId = Uuid7.New();
        await SeedResultsAsync(searchId,
            ("g-720", "The.Matrix.1999.720p.BluRay.x264-GRP"),
            ("g-1080", "The.Matrix.1999.1080p.BluRay.x264-GRP"));
        await EvaluateAsync(searchId, targetId);
        await DrainOutboxAsync();
        _sink.Clear();

        Guid blockedEvaluation;
        await using (var scope = _provider.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<DecisionDbContext>();
            blockedEvaluation = await db.ReleaseEvaluations
                .Where(e => e.SearchId == searchId && e.ReleaseGuid == "g-1080")
                .Select(e => e.Id)
                .SingleAsync();
            var blocked = await scope.ServiceProvider.GetRequiredService<IReleaseBlocklist>()
                .BlockAsync(blockedEvaluation, "Known bad encode");
            Assert.True(blocked.IsSuccess, blocked.IsFailure ? blocked.Error.Message : null);
        }

        var again = Uuid7.New();
        await SeedResultsAsync(again,
            ("g-720", "The.Matrix.1999.720p.BluRay.x264-GRP"),
            ("g-1080", "The.Matrix.1999.1080p.BluRay.x264-GRP"));
        await EvaluateAsync(again, targetId);
        await DrainOutboxAsync();

        Assert.Contains("g-720", _sink.Selected);
        Assert.DoesNotContain("g-1080", _sink.Selected);

        await using (var scope = _provider.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<DecisionDbContext>();
            var evaluation = await db.ReleaseEvaluations
                .Include(e => e.Reasons)
                .SingleAsync(e => e.SearchId == again && e.ReleaseGuid == "g-1080");
            Assert.Contains(evaluation.Reasons, reason => reason.Rule == ReleaseBlocklist.BlockedRule);
            Assert.Equal(Verdict.RejectedPermanent, evaluation.Verdict);

            var grab = await scope.ServiceProvider.GetRequiredService<IInteractiveSearch>()
                .GrabAsync(evaluation.Id);
            Assert.True(grab.IsFailure);
            Assert.Equal("decision.release_blocked", grab.Error.Code);
        }
    }

    [Fact]
    public async Task A_block_needs_a_reason_and_a_second_block_does_not_insert_another_row()
    {
        var searchId = Uuid7.New();
        await SeedResultsAsync(searchId, ("g-only", "The.Matrix.1999.1080p.BluRay.x264-GRP"));
        await EvaluateAsync(searchId, Uuid7.New());

        await using var scope = _provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<DecisionDbContext>();
        var evaluationId = await db.ReleaseEvaluations.Where(e => e.SearchId == searchId).Select(e => e.Id).SingleAsync();
        var blocks = scope.ServiceProvider.GetRequiredService<IReleaseBlocklist>();

        var missing = await blocks.BlockAsync(evaluationId, "   ");
        Assert.Equal("decision.invalid_block_reason", missing.Error.Code);

        Assert.True((await blocks.BlockAsync(evaluationId, "First reason")).IsSuccess);
        var revised = await blocks.BlockAsync(evaluationId, "Revised reason");
        Assert.True(revised.IsSuccess);
        Assert.Equal("Revised reason", revised.Value.Reason);
        Assert.Equal(1, await db.ReleaseBlocks.CountAsync(b => b.ReleaseGuid == "g-only"));
    }

    [Fact]
    public async Task Unblocking_says_so_on_the_evaluation_and_the_grab_that_follows_overrides_nothing()
    {
        var searchId = Uuid7.New();
        var targetId = Uuid7.New();
        await SeedResultsAsync(searchId, ("g-lift", "The.Matrix.1999.1080p.BluRay.x264-GRP"));
        await EvaluateAsync(searchId, targetId);
        Guid first = await EvaluationIdAsync(searchId, "g-lift");

        Guid blockId;
        await using (var scope = _provider.CreateAsyncScope())
        {
            var blocked = await scope.ServiceProvider.GetRequiredService<IReleaseBlocklist>()
                .BlockAsync(first, "Wrong cut");
            blockId = blocked.Value.Id;
        }

        // A sweep while the block stands: the profile would take it, the verdict says it was set aside.
        var again = Uuid7.New();
        await SeedResultsAsync(again, ("g-lift", "The.Matrix.1999.1080p.BluRay.x264-GRP"));
        await EvaluateAsync(again, targetId);
        var second = await EvaluationIdAsync(again, "g-lift");

        await using (var scope = _provider.CreateAsyncScope())
        {
            Assert.True((await scope.ServiceProvider.GetRequiredService<IReleaseBlocklist>().UnblockAsync(blockId)).IsSuccess);
        }

        await using (var scope = _provider.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<DecisionDbContext>();
            foreach (var evaluationId in new[] { first, second })
            {
                var rules = await db.DecisionReasons.AsNoTracking()
                    .Where(r => r.EvaluationId == evaluationId)
                    .OrderBy(r => r.Seq)
                    .Select(r => r.Rule)
                    .ToListAsync();
                Assert.Equal([ReleaseBlocklist.BlockedRule, EvaluationTrail.UnblockedRule], rules.TakeLast(2));
            }

            // Rejected only for the block that is gone: taking it now is no override of the profile.
            var grab = await scope.ServiceProvider.GetRequiredService<IInteractiveSearch>().GrabAsync(second);
            Assert.True(grab.IsSuccess, grab.IsFailure ? grab.Error.Message : null);
            Assert.Equal(Verdict.RejectedPermanent, grab.Value.Verdict);
            Assert.False(grab.Value.OverrodeVerdict);
        }

        // Blocked a second time, the trail says so again rather than trusting the first line.
        await using (var scope = _provider.CreateAsyncScope())
        {
            Assert.True((await scope.ServiceProvider.GetRequiredService<IReleaseBlocklist>().BlockAsync(first, "Still wrong")).IsSuccess);
            var db = scope.ServiceProvider.GetRequiredService<DecisionDbContext>();
            var last = await db.DecisionReasons.AsNoTracking()
                .Where(r => r.EvaluationId == first)
                .OrderByDescending(r => r.Seq)
                .Select(r => r.Rule)
                .FirstAsync();
            Assert.Equal(ReleaseBlocklist.BlockedRule, last);
        }
    }

    [Fact]
    public async Task Blocking_and_grabbing_the_same_evaluation_at_once_never_collide()
    {
        var searchId = Uuid7.New();
        var releases = Enumerable.Range(0, 8)
            .Select(i => ($"g-race-{i}", $"The.Matrix.1999.1080p.BluRay.x264-GRP{i}"))
            .ToArray();
        await SeedResultsAsync(searchId, releases);
        await EvaluateAsync(searchId, Uuid7.New());

        foreach (var (guid, _) in releases)
        {
            var evaluationId = await EvaluationIdAsync(searchId, guid);

            async Task<bool> BlockAsync()
            {
                await using var scope = _provider.CreateAsyncScope();
                var result = await scope.ServiceProvider.GetRequiredService<IReleaseBlocklist>()
                    .BlockAsync(evaluationId, "Raced");
                return result.IsSuccess;
            }

            async Task<string?> GrabAsync()
            {
                await using var scope = _provider.CreateAsyncScope();
                var result = await scope.ServiceProvider.GetRequiredService<IInteractiveSearch>()
                    .GrabAsync(evaluationId);
                return result.IsSuccess ? null : result.Error.Code;
            }

            // Either order is fine; what may not happen is both reaching for the same line number and one
            // of them throwing.
            var block = BlockAsync();
            var grab = GrabAsync();
            await Task.WhenAll(block, grab);

            Assert.True(await block);
            Assert.True(await grab is null or "decision.release_blocked");
        }
    }

    private async Task<Guid> EvaluationIdAsync(Guid searchId, string releaseGuid)
    {
        await using var scope = _provider.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<DecisionDbContext>().ReleaseEvaluations
            .Where(e => e.SearchId == searchId && e.ReleaseGuid == releaseGuid)
            .Select(e => e.Id)
            .SingleAsync();
    }

    private async Task SeedResultsAsync(Guid searchId, params (string Guid, string Title)[] results)
    {
        await using var scope = _provider.CreateAsyncScope();
        var discoveryDb = scope.ServiceProvider.GetRequiredService<DiscoveryDbContext>();
        var now = DateTimeOffset.UtcNow;
        discoveryDb.SearchExecutions.Add(new SearchExecution
        {
            Id = searchId,
            Term = "The Matrix",
            ContentKind = "Movie",
            StartedAt = now,
            CompletedAt = now,
            ResultCount = results.Length,
        });
        foreach (var (guid, title) in results)
        {
            discoveryDb.SearchResults.Add(new SearchResult
            {
                Id = Uuid7.New(),
                ExecutionId = searchId,
                FoundAt = now,
                ReleaseGuid = guid,
                Title = title,
                DownloadUrl = $"magnet:?xt=urn:btih:{guid}",
                Protocol = ReleaseProtocol.Torrent,
                SizeBytes = 5_000_000_000,
                Seeders = 10,
                IndexerName = "Idx",
            });
        }

        await discoveryDb.SaveChangesAsync();
    }

    private async Task EvaluateAsync(Guid searchId, Guid targetId)
    {
        await using var scope = _provider.CreateAsyncScope();
        var engine = scope.ServiceProvider.GetRequiredService<DecisionEngine>();
        await engine.EvaluateSearchAsync(searchId, targetId);
    }

    private async Task DrainOutboxAsync()
    {
        await using var scope = _provider.CreateAsyncScope();
        var relay = scope.ServiceProvider.GetRequiredService<OutboxRelay>();
        while (await relay.ProcessBatchAsync() > 0)
        {
        }
    }

    private sealed class UnusedPlans : ITargetSearchPlans
    {
        public Task<TargetSearchPlan?> ResolveAsync(MonitoredTargetId targetId, CancellationToken cancellationToken = default) =>
            Task.FromResult<TargetSearchPlan?>(null);
    }

    private sealed class EventSink
    {
        public ConcurrentBag<string> Selected { get; } = [];

        public void Clear()
        {
            while (Selected.TryTake(out _))
            {
            }
        }
    }

    private sealed class ReleaseSelectedSink(EventSink sink) : IEventHandler<ReleaseSelected>
    {
        public Task HandleAsync(ReleaseSelected domainEvent, CancellationToken cancellationToken = default)
        {
            sink.Selected.Add(domainEvent.ReleaseGuid);
            return Task.CompletedTask;
        }
    }

    private sealed class NoAcceptableSink : IEventHandler<NoAcceptableRelease>
    {
        public Task HandleAsync(NoAcceptableRelease domainEvent, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
    }
}
