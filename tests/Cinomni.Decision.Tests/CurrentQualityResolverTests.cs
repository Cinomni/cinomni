using Cinomni.Decision.Evaluation;
using Cinomni.Library.Contracts;
using Cinomni.ReleaseParsing.Contracts;
using static Cinomni.Decision.Tests.DecisionFixtures;

namespace Cinomni.Decision.Tests;

/// <summary>
/// Turning "what is already on disk" into the baseline a candidate has to beat.
/// </summary>
public sealed class CurrentQualityResolverTests
{
    private static readonly Guid Unit1 = Guid.Parse("11111111-1111-4111-8111-111111111111");
    private static readonly Guid Unit2 = Guid.Parse("22222222-2222-4222-8222-222222222222");

    private static AcquisitionProfileFixture Standard() => new();

    [Fact]
    public async Task Ranks_what_is_on_disk_by_the_profile()
    {
        var resolver = ResolverFor((Unit1, new ReleaseQuality("Bluray", "R1080p", "None", 1)));

        var current = await resolver.ResolveAsync(Standard().Profile, [Unit1]);

        Assert.NotNull(current);
        Assert.Equal(50, current.Rank);
    }

    [Fact]
    public async Task Reads_the_resolution_label_library_derives_from_the_file()
    {
        // Library writes what it measured off the video stream ("1080p"); the profile speaks the enum's
        // name ("R1080p"). Failing to reconcile them would silently rank every imported file as unknown
        // and offer to replace the whole library.
        var resolver = ResolverFor((Unit1, new ReleaseQuality("Bluray", "1080p", null, 1)));

        var current = await resolver.ResolveAsync(Standard().Profile, [Unit1]);

        Assert.Equal(50, current?.Rank);
    }

    [Fact]
    public async Task Has_no_baseline_when_a_unit_holds_nothing()
    {
        // Not an upgrade but a gap: whatever is acceptable is welcome.
        var resolver = ResolverFor((Unit1, new ReleaseQuality("Bluray", "R1080p", null, 1)));

        Assert.Null(await resolver.ResolveAsync(Standard().Profile, [Unit1, Unit2]));
    }

    [Fact]
    public async Task Has_no_baseline_when_the_stored_quality_is_not_readable()
    {
        // An honest "we do not know" rather than a guess that would replace a good file.
        var resolver = ResolverFor((Unit1, new ReleaseQuality("Nonsense", "R1080p", null, 1)));

        var current = await resolver.ResolveAsync(Standard().Profile, [Unit1]);

        Assert.Equal(int.MinValue, current?.Rank);
    }

    [Fact]
    public async Task The_weakest_unit_sets_the_bar_for_a_pack()
    {
        // A pack that betters the worst episode is worth having even if it only matches the rest.
        var resolver = ResolverFor(
            (Unit1, new ReleaseQuality("Bluray", "R1080p", null, 1)),
            (Unit2, new ReleaseQuality("WebDl", "R720p", null, 1)));

        var current = await resolver.ResolveAsync(Standard().Profile, [Unit1, Unit2]);

        Assert.Equal(25, current?.Rank);
    }

    [Fact]
    public async Task Ranks_a_quality_the_profile_no_longer_allows_below_everything_it_does()
    {
        // The owner removed that quality from the allowed set; reading that as "anything allowed is
        // better" is what they asked for.
        var resolver = ResolverFor((Unit1, new ReleaseQuality("Dvd", "R480p", null, 1)));

        var current = await resolver.ResolveAsync(Standard().Profile, [Unit1]);

        Assert.Equal(int.MinValue, current?.Rank);
    }

    [Fact]
    public async Task A_search_for_no_particular_unit_has_no_baseline()
    {
        // A manual search resolves no units, and there is nothing to compare against.
        var resolver = ResolverFor((Unit1, new ReleaseQuality("Bluray", "R1080p", null, 1)));

        Assert.Null(await resolver.ResolveAsync(Standard().Profile, []));
    }

    private static CurrentQualityResolver ResolverFor(params (Guid UnitId, ReleaseQuality Quality)[] onDisk)
    {
        var library = new FakeLibraryQuality();
        foreach (var (unitId, quality) in onDisk)
        {
            library.Holding(unitId, quality);
        }

        return new CurrentQualityResolver(library);
    }

    /// <summary>A profile with two allowed qualities, so "better" and "worse" both exist.</summary>
    private sealed class AcquisitionProfileFixture
    {
        public Persistence.AcquisitionProfile Profile { get; } = DecisionFixtures.Profile()
            .WithQuality(QualitySource.Bluray, QualityResolution.R1080p, rank: 50)
            .WithQuality(QualitySource.WebDl, QualityResolution.R720p, rank: 25);
    }
}
