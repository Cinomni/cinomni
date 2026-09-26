using Cinomni.Kernel.Messaging;
using Cinomni.ReleaseParsing.Messaging;
using Cinomni.ReleaseParsing.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Cinomni.ReleaseParsing.Tests;

/// <summary>
/// Integration tests for the parse-audit retention sweep against a real PostgreSQL instance: old
/// records go, recent ones stay, the rule-set versions are untouched, and — the property that makes
/// this purge safe at all — a purged title parsed again is simply recreated.
/// </summary>
public sealed class ParseRetentionTests : IAsyncLifetime
{
    private const string Title = "The.Matrix.1999.1080p.BluRay.x264-SPARKS";
    private static readonly TimeSpan Window = TimeSpan.FromDays(180);

    private ServiceProvider _provider = null!;

    public async Task InitializeAsync() =>
        _provider = await ReleaseParsingTestHost.CreateAsync("cinomni_test_parsing_retention");

    public async Task DisposeAsync() => await _provider.DisposeAsync();

    [Fact]
    public async Task Old_records_are_removed_and_recent_ones_survive()
    {
        await SeedRuleVersionAsync();
        await ParseAsync(Title);
        await ParseAsync("Interstellar.2014.2160p.WEB-DL.x265-GROUP");

        // Age exactly one of them past the window.
        await using (var scope = _provider.CreateAsyncScope())
        {
            var dbContext = scope.ServiceProvider.GetRequiredService<ReleaseParsingDbContext>();
            await dbContext.ParsedReleases
                .Where(p => p.SourceTitle == Title)
                .ExecuteUpdateAsync(p => p.SetProperty(
                    x => x.CreatedAt, DateTimeOffset.UtcNow - Window - TimeSpan.FromDays(1)));
        }

        await PurgeAsync();

        await using (var scope = _provider.CreateAsyncScope())
        {
            var dbContext = scope.ServiceProvider.GetRequiredService<ReleaseParsingDbContext>();
            Assert.False(await dbContext.ParsedReleases.AnyAsync(p => p.SourceTitle == Title));
            Assert.Equal(1, await dbContext.ParsedReleases.CountAsync());
            // The rule-set versions are the audit boundary, not history: even one older than the
            // window survives.
            Assert.True(await dbContext.ParseRuleVersions.AnyAsync());
        }
    }

    private async Task SeedRuleVersionAsync()
    {
        await using var scope = _provider.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<ReleaseParsingDbContext>();
        dbContext.ParseRuleVersions.Add(new ParseRuleVersion
        {
            Version = "0.9.0",
            Notes = "Seeded by the retention test.",
            CreatedAt = DateTimeOffset.UtcNow - Window - TimeSpan.FromDays(400),
        });
        await dbContext.SaveChangesAsync();
    }

    [Fact]
    public async Task A_purged_title_is_recreated_when_it_is_parsed_again()
    {
        await ParseAsync(Title);

        await using (var scope = _provider.CreateAsyncScope())
        {
            var dbContext = scope.ServiceProvider.GetRequiredService<ReleaseParsingDbContext>();
            await dbContext.ParsedReleases.ExecuteUpdateAsync(p => p.SetProperty(
                x => x.CreatedAt, DateTimeOffset.UtcNow - Window - TimeSpan.FromDays(30)));
        }

        await PurgeAsync();
        await PurgeAsync();

        await ParseAsync(Title);

        await using (var scope = _provider.CreateAsyncScope())
        {
            var dbContext = scope.ServiceProvider.GetRequiredService<ReleaseParsingDbContext>();
            var record = await dbContext.ParsedReleases.AsNoTracking().SingleAsync();
            Assert.Equal(Title, record.SourceTitle);
            Assert.Equal("the-matrix.1999.1080p.bluray", record.CanonicalKey);
        }
    }

    private async Task ParseAsync(string title)
    {
        await using var scope = _provider.CreateAsyncScope();
        var handler = scope.ServiceProvider.GetRequiredService<ICommandHandler<ParseAndPersistCommand>>();
        Assert.True((await handler.HandleAsync(new ParseAndPersistCommand(title))).IsSuccess);
    }

    private async Task PurgeAsync()
    {
        await using var scope = _provider.CreateAsyncScope();
        var handler = scope.ServiceProvider.GetRequiredService<ICommandHandler<PurgeParsedReleasesCommand>>();
        Assert.True((await handler.HandleAsync(new PurgeParsedReleasesCommand())).IsSuccess);
    }
}
