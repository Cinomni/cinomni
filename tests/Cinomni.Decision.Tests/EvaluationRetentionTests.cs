using Cinomni.Decision.Contracts;
using Cinomni.Decision.Messaging;
using Cinomni.Decision.Persistence;
using Cinomni.Kernel.Identifiers;
using Cinomni.Kernel.Messaging;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Cinomni.Decision.Tests;

/// <summary>
/// Integration tests for the evaluation retention sweep against a real PostgreSQL instance. The
/// point of these is not size: it is the product guarantee that an explanation never disappears.
/// An accepted verdict and a manual override survive at any age; only old rejections nobody acted on
/// are forgotten.
/// </summary>
public sealed class EvaluationRetentionTests : IAsyncLifetime
{
    private static readonly TimeSpan Window = TimeSpan.FromDays(90);

    private ServiceProvider _provider = null!;

    public async Task InitializeAsync() =>
        _provider = await DecisionTestHost.CreateAsync("cinomni_test_decision_retention");

    public async Task DisposeAsync() => await _provider.DisposeAsync();

    [Fact]
    public async Task Only_old_rejections_nobody_acted_on_are_removed()
    {
        var ancient = DateTimeOffset.UtcNow - Window - TimeSpan.FromDays(110);
        var recent = DateTimeOffset.UtcNow - TimeSpan.FromDays(5);

        var accepted = await SeedAsync(Verdict.Accepted, ancient, manuallyOverridden: false);
        var overridden = await SeedAsync(Verdict.RejectedPermanent, ancient, manuallyOverridden: true);
        var recentRejection = await SeedAsync(Verdict.RejectedPermanent, recent, manuallyOverridden: false);
        var forgettable = await SeedAsync(Verdict.RejectedPermanent, ancient, manuallyOverridden: false);

        await PurgeAsync();

        await using var scope = _provider.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<DecisionDbContext>();

        Assert.True(await dbContext.ReleaseEvaluations.AnyAsync(e => e.Id == accepted));
        Assert.True(await dbContext.ReleaseEvaluations.AnyAsync(e => e.Id == overridden));
        Assert.True(await dbContext.ReleaseEvaluations.AnyAsync(e => e.Id == recentRejection));
        Assert.False(await dbContext.ReleaseEvaluations.AnyAsync(e => e.Id == forgettable));

        // The reasons are the explanation; they follow the evaluation through the schema's cascade.
        Assert.False(await dbContext.DecisionReasons.AnyAsync(r => r.EvaluationId == forgettable));
        Assert.True(await dbContext.DecisionReasons.AnyAsync(r => r.EvaluationId == accepted));
        Assert.True(await dbContext.DecisionReasons.AnyAsync(r => r.EvaluationId == overridden));
    }

    [Fact]
    public async Task Explanations_survive_however_often_the_sweep_runs()
    {
        var ancient = DateTimeOffset.UtcNow - Window - TimeSpan.FromDays(300);
        var accepted = await SeedAsync(Verdict.Accepted, ancient, manuallyOverridden: false);
        var overridden = await SeedAsync(Verdict.RejectedPermanent, ancient, manuallyOverridden: true);

        for (var run = 0; run < 3; run++)
        {
            await PurgeAsync();
        }

        await using var scope = _provider.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<DecisionDbContext>();
        Assert.Equal(2, await dbContext.ReleaseEvaluations.CountAsync(e => e.Id == accepted || e.Id == overridden));
    }

    private async Task PurgeAsync()
    {
        await using var scope = _provider.CreateAsyncScope();
        var handler = scope.ServiceProvider.GetRequiredService<ICommandHandler<PurgeEvaluationsCommand>>();
        Assert.True((await handler.HandleAsync(new PurgeEvaluationsCommand())).IsSuccess);
    }

    private async Task<Guid> SeedAsync(Verdict verdict, DateTimeOffset createdAt, bool manuallyOverridden)
    {
        await using var scope = _provider.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<DecisionDbContext>();

        var profileId = await dbContext.Profiles.Select(p => p.Id).FirstAsync();
        var evaluationId = Uuid7.New();

        var evaluation = new ReleaseEvaluationRecord
        {
            Id = evaluationId,
            ProfileId = profileId,
            SearchId = Uuid7.New(),
            TargetId = Uuid7.New(),
            ReleaseGuid = $"guid-{evaluationId}",
            ReleaseTitle = "Some Release 2026 1080p",
            CanonicalKey = null,
            Verdict = verdict,
            CustomFormatScore = 0,
            EvaluatorVersion = "test",
            CreatedAt = createdAt,
        };

        evaluation.Reasons.Add(new DecisionReasonRecord
        {
            EvaluationId = evaluationId,
            Seq = 0,
            Rule = "Quality",
            Property = "resolution",
            ProfileValue = "1080p",
            ActualValue = "1080p",
            Outcome = ReasonOutcome.Pass,
        });

        if (manuallyOverridden)
        {
            // The exact rule name the interactive search appends when an administrator takes a
            // release the profile refused.
            evaluation.Reasons.Add(new DecisionReasonRecord
            {
                EvaluationId = evaluationId,
                Seq = 1,
                Rule = "ManualOverride",
                Property = "verdict",
                ProfileValue = verdict.ToString(),
                ActualValue = "selected by an administrator",
                Outcome = ReasonOutcome.Pass,
            });
        }

        dbContext.ReleaseEvaluations.Add(evaluation);
        await dbContext.SaveChangesAsync();
        return evaluationId;
    }
}
