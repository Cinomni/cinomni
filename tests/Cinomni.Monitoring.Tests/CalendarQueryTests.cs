using Cinomni.Catalog.Contracts;
using Cinomni.Kernel.Security;
using Cinomni.Monitoring.Application;
using Cinomni.Monitoring.Contracts;
using Cinomni.Monitoring.Persistence;
using Microsoft.Extensions.DependencyInjection;

namespace Cinomni.Monitoring.Tests;

public sealed class CalendarQueryTests : IAsyncLifetime
{
    private ServiceProvider _host = null!;

    public async Task InitializeAsync() =>
        _host = await MonitoringTestHost.CreateAsync("cinomni_test_monitoring_calendar");

    public async Task DisposeAsync() => await _host.DisposeAsync();

    [Fact]
    public async Task The_window_returns_an_episode_on_that_date_and_not_one_outside_it()
    {
        var today = new DateOnly(2026, 9, 22);
        await using (var scope = _host.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<MonitoringDbContext>();
            db.MonitoredTargets.Add(Target(today, 1));
            db.MonitoredTargets.Add(Target(today.AddDays(30), 2));
            await db.SaveChangesAsync();
        }

        await using var read = _host.CreateAsyncScope();
        var query = read.ServiceProvider.GetRequiredService<IMonitoringQuery>();
        var airing = await query.ListAiringAsync(today, today.AddDays(6));

        var only = Assert.Single(airing);
        Assert.Equal(1, only.EpisodeNumber);
        Assert.Equal(today, only.PublishedAirDate);
    }

    [Fact]
    public async Task A_movie_is_not_an_airing_even_with_a_date_on_its_target()
    {
        // Nothing gives a movie target a date today; if one ever carried one, it still is not an episode
        // airing, and the contract no longer claims it is.
        var today = new DateOnly(2026, 10, 5);
        await using (var scope = _host.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<MonitoringDbContext>();
            var movie = Target(today, 1);
            db.MonitoredTargets.Add(new MonitoredTarget
            {
                Id = Guid.NewGuid(),
                WorkId = Guid.NewGuid(),
                Kind = TargetKind.Movie,
                TargetRef = Guid.NewGuid(),
                Monitored = true,
                Mode = MonitoringMode.All,
                IsMissing = true,
                PublishedAirDate = today,
                AirDate = movie.AirDate,
                CreatedAt = DateTimeOffset.UtcNow,
            });
            db.MonitoredTargets.Add(movie);
            await db.SaveChangesAsync();
        }

        await using var read = _host.CreateAsyncScope();
        var airing = await read.ServiceProvider.GetRequiredService<IMonitoringQuery>().ListAiringAsync(today, today);

        Assert.Equal(TargetKind.Episode, Assert.Single(airing).Kind);
    }

    [Fact]
    public async Task The_last_representable_day_does_not_overflow_the_read()
    {
        await using var read = _host.CreateAsyncScope();
        var airing = await read.ServiceProvider.GetRequiredService<IMonitoringQuery>()
            .ListAiringAsync(DateOnly.MaxValue.AddDays(-3), DateOnly.MaxValue);

        Assert.Empty(airing);
    }

    [Fact]
    public async Task A_member_is_not_shown_a_short_page_because_hidden_titles_filled_the_first_one()
    {
        // Arrange — a full page of airings of a title the member may not see, then a few they may.
        var member = new Viewer(Guid.NewGuid(), IsAdministrator: false);
        var today = new DateOnly(2026, 9, 22);
        Guid hiddenWork;
        Guid openWork;
        await using (var scope = _host.CreateAsyncScope())
        {
            var catalog = scope.ServiceProvider.GetRequiredService<ICatalogCommands>();
            hiddenWork = (await catalog.AddSeriesAsync("Grown-up Show", 2020, [])).Value.Value;
            openWork = (await catalog.AddSeriesAsync("Family Show", 2021, [])).Value.Value;
            var collections = scope.ServiceProvider.GetRequiredService<ICollectionAdministration>();
            var shelf = await collections.CreateAsync("Grown-ups", CollectionKind.Series, CollectionAccessMode.Restricted);
            Assert.True((await collections.MoveWorkAsync(new WorkId(hiddenWork), shelf.Value)).IsSuccess);

            var db = scope.ServiceProvider.GetRequiredService<MonitoringDbContext>();
            for (var episode = 1; episode <= MonitoringPaging.MaxPageSize; episode++)
            {
                db.MonitoredTargets.Add(Target(today, episode, hiddenWork));
            }

            for (var episode = 1; episode <= 3; episode++)
            {
                db.MonitoredTargets.Add(Target(today.AddDays(1), episode, openWork));
            }

            await db.SaveChangesAsync();
        }

        // Act
        await using var read = _host.CreateAsyncScope();
        var entries = await read.ServiceProvider.GetRequiredService<CalendarReader>().ListAsync(member, today, today.AddDays(6));

        // Assert — the three the member may see, with the title Catalog gives them.
        Assert.Equal(3, entries.Count);
        Assert.All(entries, entry => Assert.Equal("Family Show", entry.WorkTitle));
    }

    private static MonitoredTarget Target(DateOnly airDate, int episode, Guid? workId = null) => new()
    {
        Id = Guid.NewGuid(),
        WorkId = workId ?? Guid.NewGuid(),
        Kind = TargetKind.Episode,
        TargetRef = Guid.NewGuid(),
        Monitored = true,
        Mode = MonitoringMode.All,
        IsMissing = true,
        SeasonNumber = 1,
        EpisodeNumber = episode,
        PublishedAirDate = airDate,
        AirDate = new DateTimeOffset(airDate.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero),
        EpisodeTitle = $"Episode {episode}",
        CreatedAt = DateTimeOffset.UtcNow,
    };
}
