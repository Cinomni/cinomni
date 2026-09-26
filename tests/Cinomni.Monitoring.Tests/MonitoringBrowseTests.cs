using Cinomni.Catalog.Contracts;
using Cinomni.Kernel.Identifiers;
using Cinomni.Kernel.Security;
using Cinomni.Monitoring.Application;
using Cinomni.Monitoring.Contracts;
using Microsoft.Extensions.DependencyInjection;

namespace Cinomni.Monitoring.Tests;

/// <summary>
/// Abuse cases for the two global listings' authorization defect: a target row carries a work id, and a
/// work sits in a collection that may be restricted, so an unfiltered listing let a member enumerate
/// titles they were never granted. <see cref="MonitoringBrowse"/> is what closes it — these tests are what
/// actually pins the fix, since <c>ApiAuthorizationTests</c> can only see that the route stays
/// authenticated-but-not-administrator, never whether a filter was written.
/// <para>
/// Its own database — every <c>TestHost</c> opens with <c>EnsureDeletedAsync</c>, so sharing a name with
/// another suite would drop that suite's database mid-run.
/// </para>
/// </summary>
public sealed class MonitoringBrowseTests : IAsyncLifetime
{
    private static readonly Viewer Administrator = new(Uuid7.New(), IsAdministrator: true);
    private static readonly Viewer Member = new(Uuid7.New(), IsAdministrator: false);

    private ServiceProvider _host = null!;
    private MonitoringDriver _driver = null!;

    public async Task InitializeAsync()
    {
        _host = await MonitoringTestHost.CreateAsync("cinomni_test_monitoring_browse");
        _driver = new MonitoringDriver(_host);
    }

    public async Task DisposeAsync() => await _host.DisposeAsync();

    [Fact]
    public async Task A_member_without_a_grant_sees_neither_listing_carry_the_restricted_targets()
    {
        var openWorkId = await _driver.AddMovieAsync("Heat");
        var shelf = await _driver.CreateRestrictedCollectionAsync("Grown-ups");
        var restrictedWorkId = await _driver.AddMovieAsync("Alien", collection: shelf);
        await _driver.DrainAsync();

        var all = await ListAsync(Member);
        Assert.Contains(all, t => t.WorkId == openWorkId);
        Assert.DoesNotContain(all, t => t.WorkId == restrictedWorkId);

        var missing = await ListMissingAsync(Member, workId: null);
        Assert.Contains(missing, t => t.WorkId == openWorkId);
        Assert.DoesNotContain(missing, t => t.WorkId == restrictedWorkId);

        // Not yours reads exactly like not found: scoping the missing list to the hidden work by id
        // answers empty, never a distinct "forbidden".
        Assert.Empty(await ListMissingAsync(Member, workId: restrictedWorkId));
    }

    [Fact]
    public async Task A_member_granted_the_collection_sees_its_targets_in_both_listings()
    {
        var shelf = await _driver.CreateRestrictedCollectionAsync("Grown-ups");
        var restrictedWorkId = await _driver.AddMovieAsync("Alien", collection: shelf);
        await _driver.DrainAsync();
        await _driver.GrantCollectionAsync(shelf, Member.UserId, Administrator.UserId);

        var all = await ListAsync(Member);
        Assert.Contains(all, t => t.WorkId == restrictedWorkId);

        var missing = await ListMissingAsync(Member, workId: null);
        Assert.Contains(missing, t => t.WorkId == restrictedWorkId);

        Assert.NotEmpty(await ListMissingAsync(Member, workId: restrictedWorkId));
    }

    [Fact]
    public async Task An_administrator_sees_every_target_in_both_listings()
    {
        var openWorkId = await _driver.AddMovieAsync("Heat");
        var shelf = await _driver.CreateRestrictedCollectionAsync("Grown-ups");
        var restrictedWorkId = await _driver.AddMovieAsync("Alien", collection: shelf);
        await _driver.DrainAsync();

        var all = await ListAsync(Administrator);
        Assert.Contains(all, t => t.WorkId == openWorkId);
        Assert.Contains(all, t => t.WorkId == restrictedWorkId);

        var missing = await ListMissingAsync(Administrator, workId: null);
        Assert.Contains(missing, t => t.WorkId == openWorkId);
        Assert.Contains(missing, t => t.WorkId == restrictedWorkId);
    }

    [Fact]
    public async Task Paging_stays_honest_when_a_restricted_title_sits_in_the_middle_of_the_page()
    {
        // Three open movies, a restricted one in the middle of creation order, then three more open
        // movies: seven rows total, six of them visible to Member. A page cut before filtering would come
        // back short (five rows) or would leak the hidden row; the correct behaviour is a full page of six.
        var shelf = await _driver.CreateRestrictedCollectionAsync("Grown-ups");
        var openWorkIds = new List<WorkId>();

        for (var i = 0; i < 3; i++)
        {
            openWorkIds.Add(await _driver.AddMovieAsync($"Open {i}"));
        }

        var restrictedWorkId = await _driver.AddMovieAsync("Hidden", collection: shelf);

        for (var i = 3; i < 6; i++)
        {
            openWorkIds.Add(await _driver.AddMovieAsync($"Open {i}"));
        }

        await _driver.DrainAsync();

        var page = await ListAsync(Member, limit: 6, offset: 0);
        Assert.Equal(6, page.Count);
        Assert.DoesNotContain(page, t => t.WorkId == restrictedWorkId);
        Assert.Equal(openWorkIds.ToHashSet(), page.Select(t => t.WorkId).ToHashSet());

        var missingPage = await ListMissingAsync(Member, limit: 6);
        Assert.Equal(6, missingPage.Count);
        Assert.DoesNotContain(missingPage, t => t.WorkId == restrictedWorkId);
        Assert.Equal(openWorkIds.ToHashSet(), missingPage.Select(t => t.WorkId).ToHashSet());
    }

    private async Task<IReadOnlyList<MonitoredTargetSummary>> ListAsync(Viewer viewer, int limit = 100, int offset = 0)
    {
        await using var scope = _host.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<MonitoringBrowse>().ListAsync(viewer, limit, offset);
    }

    private async Task<IReadOnlyList<MonitoredTargetSummary>> ListMissingAsync(
        Viewer viewer, WorkId? workId = null, int limit = 100)
    {
        await using var scope = _host.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<MonitoringBrowse>()
            .ListMissingAsync(viewer, limit, workId);
    }
}
