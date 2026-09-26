using System.Text.Json;
using Cinomni.Decision.Application;
using Cinomni.Decision.Contracts;
using Cinomni.Decision.Persistence;
using Cinomni.Kernel.Identifiers;
using Cinomni.Library.Contracts;
using Cinomni.Operations.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Cinomni.Decision.Tests;

/// <summary>
/// Judging what an import landed and announcing the verdict — the step that turns a profile's cutoff
/// into something the sweep can act on without consulting Decision on every tick.
/// </summary>
public sealed class UpgradeAssessmentTests : IAsyncLifetime
{
    private static readonly Guid WorkId = Guid.Parse("aaaaaaaa-1111-4111-8111-aaaaaaaaaaaa");

    /// <summary>Ranks from the seeded movie profile, which these tests configure rather than replace.</summary>
    private const int BlurayRank = 50;

    private readonly FakeLibraryQuality _library = new();
    private ServiceProvider _provider = null!;

    public async Task InitializeAsync() =>
        _provider = await DecisionTestHost.CreateAsync(
            "cinomni_test_decision_upgrade",
            services => services.AddSingleton<ILibraryQuery>(_library));

    public async Task DisposeAsync() => await _provider.DisposeAsync();

    [Fact]
    public async Task Announces_that_a_title_below_the_cutoff_wants_a_better_release()
    {
        await SeedProfileAsync(cutoffRank: BlurayRank, upgradesAllowed: true);
        _library.Holding(WorkId, new ReleaseQuality("WebDl", "R720p", null, 1));

        var assessed = await AssessAsync();

        Assert.Equal([WorkId], assessed.UnitsWantingUpgrade);
        Assert.Empty(assessed.UnitsSatisfied);
    }

    [Fact]
    public async Task Announces_that_a_title_at_the_cutoff_is_done()
    {
        await SeedProfileAsync(cutoffRank: BlurayRank, upgradesAllowed: true);
        _library.Holding(WorkId, new ReleaseQuality("Bluray", "R1080p", null, 1));

        var assessed = await AssessAsync();

        Assert.Empty(assessed.UnitsWantingUpgrade);
        Assert.Equal([WorkId], assessed.UnitsSatisfied);
    }

    [Fact]
    public async Task Wants_nothing_when_the_profile_forbids_upgrades()
    {
        // The state every profile is migrated into, and the reason an existing library stays quiet
        // until its owner opts in.
        await SeedProfileAsync(cutoffRank: BlurayRank, upgradesAllowed: false);
        _library.Holding(WorkId, new ReleaseQuality("WebDl", "R720p", null, 1));

        Assert.Empty((await AssessAsync()).UnitsWantingUpgrade);
    }

    [Fact]
    public async Task Wants_nothing_for_a_file_whose_quality_cannot_be_read()
    {
        // Everything imported before the release name was carried through. Judging it would mean
        // replacing a working file on the strength of a guess.
        await SeedProfileAsync(cutoffRank: BlurayRank, upgradesAllowed: true);

        var assessed = await AssessAsync();

        Assert.Empty(assessed.UnitsWantingUpgrade);
        Assert.Equal([WorkId], assessed.UnitsSatisfied);
    }

    /// <summary>Runs the assessment and returns the single event it wrote to the outbox.</summary>
    private async Task<UpgradeAssessed> AssessAsync()
    {
        var assetId = Uuid7.New();
        await using (var scope = _provider.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<UpgradeAssessment>()
                .AssessAsync(assetId, WorkId, [WorkId]);
        }

        await using var read = _provider.CreateAsyncScope();
        var outbox = read.ServiceProvider.GetRequiredService<OperationsDbContext>();
        var message = await outbox.Outbox
            .Where(m => m.EventType == DecisionEventNames.UpgradeAssessed)
            .OrderByDescending(m => m.OccurredAt)
            .FirstOrDefaultAsync();

        Assert.NotNull(message);
        // The outbox serializes with the platform's own casing; read it back on the same terms.
        var assessed = JsonSerializer.Deserialize<UpgradeAssessed>(
            message!.Payload, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        Assert.NotNull(assessed);
        return assessed!;
    }

    /// <summary>
    /// Configures the movie profile the module seeds, rather than adding a second one — that is what an
    /// administrator actually does, and a rival profile would not be the one the assessment picks.
    /// </summary>
    private async Task SeedProfileAsync(int cutoffRank, bool upgradesAllowed)
    {
        await using var scope = _provider.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<DecisionDbContext>();

        var profile = await dbContext.Profiles
            .Include(p => p.AllowedQualities)
            .OrderBy(p => p.CreatedAt)
            .FirstAsync(p => p.AppliesTo == ProfileScope.Movie);

        profile.CutoffRank = cutoffRank;
        profile.UpgradesAllowed = upgradesAllowed;

        await dbContext.SaveChangesAsync();
    }
}
