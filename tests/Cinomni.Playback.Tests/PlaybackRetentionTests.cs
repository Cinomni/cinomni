using Cinomni.Kernel.Messaging;
using Cinomni.Playback.Application;
using Cinomni.Playback.Contracts;
using Cinomni.Playback.Messaging;
using Cinomni.Playback.Persistence;
using Cinomni.Playback.Planning;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Cinomni.Playback.Tests;

/// <summary>
/// Integration tests for the playback-session retention sweep against a real PostgreSQL instance:
/// finished sessions and their transcode jobs age out together with the segments they left on disk, a
/// session that is actually in flight never does, a session nobody ever ended stops accumulating, and
/// — the assertion that matters most — resume progress is untouched at any age, because it is state
/// rather than history and it is what "continue watching" reads.
/// </summary>
public sealed class PlaybackRetentionTests : IAsyncLifetime, IDisposable
{
    private static readonly TimeSpan Window = TimeSpan.FromDays(90);

    private readonly FakeMediaEncoder _encoder = new();

    /// <summary>A real directory tree, so the reclaim is asserted against the filesystem, not a mock.</summary>
    private readonly string _transcodeRoot = Path.Combine(
        Path.GetTempPath(), "cinomni-playback-retention", Guid.NewGuid().ToString("N"));

    private ServiceProvider _provider = null!;

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(_transcodeRoot);
        _provider = await PlaybackTestHost.CreateAsync(
            "cinomni_test_playback_retention",
            _encoder,
            services => services.AddSingleton(new PlaybackOptions { TranscodeRoot = _transcodeRoot }));
    }

    public async Task DisposeAsync() => await _provider.DisposeAsync();

    public void Dispose()
    {
        if (Directory.Exists(_transcodeRoot))
        {
            Directory.Delete(_transcodeRoot, recursive: true);
        }
    }

    [Fact]
    public async Task Finished_sessions_age_out_with_their_transcode_jobs()
    {
        var stale = await SeedSessionAsync(finished: true, withTranscodeJob: true);
        var recent = await SeedSessionAsync(finished: true, withTranscodeJob: true);
        var inFlight = await SeedSessionAsync(finished: false, withTranscodeJob: true);

        await AgeAsync(stale, DateTimeOffset.UtcNow - Window - TimeSpan.FromDays(1));

        await PurgeAsync();

        await using var scope = _provider.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<PlaybackDbContext>();

        Assert.False(await dbContext.Sessions.AnyAsync(s => s.Id == stale));
        Assert.False(await dbContext.TranscodeJobs.AnyAsync(j => j.SessionId == stale));

        Assert.True(await dbContext.Sessions.AnyAsync(s => s.Id == recent));
        // A session that reported progress recently is live work, whatever state it is in.
        Assert.True(await dbContext.Sessions.AnyAsync(s => s.Id == inFlight));
    }

    /// <summary>
    /// A client that closes the tab never tells the server, so the session stays in a delivery state
    /// for ever. Those rows — and the segment directories they own — are precisely the ones that
    /// accumulate, so silence past the window retires them too.
    /// </summary>
    [Fact]
    public async Task A_session_that_never_ended_is_reclaimed_once_it_has_been_silent_past_the_window()
    {
        var abandoned = await SeedSessionAsync(finished: false, withTranscodeJob: true);
        var live = await SeedSessionAsync(finished: false, withTranscodeJob: true);

        await AgeAsync(abandoned, DateTimeOffset.UtcNow - Window - TimeSpan.FromDays(1));
        await AgeAsync(live, DateTimeOffset.UtcNow - TimeSpan.FromMinutes(5));

        await PurgeAsync();

        await using var scope = _provider.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<PlaybackDbContext>();

        Assert.False(await dbContext.Sessions.AnyAsync(s => s.Id == abandoned));
        Assert.True(await dbContext.Sessions.AnyAsync(s => s.Id == live));
    }

    /// <summary>
    /// The session row is the only thing that names its output directory, so the decoded copy of the
    /// library file has to go before the row does — otherwise it outlives its retention window on disk
    /// with nothing left in the database able to find it.
    /// </summary>
    [Fact]
    public async Task The_transcode_directory_goes_with_the_session_and_nothing_else_does()
    {
        var stale = await SeedSessionAsync(finished: true, withTranscodeJob: true);
        var recent = await SeedSessionAsync(finished: true, withTranscodeJob: true);

        var staleOutput = SeedOutputDirectory(stale);
        var recentOutput = SeedOutputDirectory(recent);
        var unrelated = Path.Combine(_transcodeRoot, "not-a-session");
        Directory.CreateDirectory(unrelated);

        await AgeAsync(stale, DateTimeOffset.UtcNow - Window - TimeSpan.FromDays(1));

        await PurgeAsync();
        // Idempotent: the directory is already gone on the second pass and nothing fails.
        await PurgeAsync();

        Assert.False(Directory.Exists(staleOutput));
        Assert.True(Directory.Exists(recentOutput));
        Assert.True(Directory.Exists(unrelated));
        Assert.True(Directory.Exists(_transcodeRoot));
    }

    /// <summary>Writes a plausible HLS output tree for a session, as FFmpeg would leave it.</summary>
    private string SeedOutputDirectory(Guid sessionId)
    {
        var directory = Path.Combine(_transcodeRoot, sessionId.ToString("D"));
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "index.m3u8"), "#EXTM3U\n");
        File.WriteAllBytes(Path.Combine(directory, "segment0.ts"), [0x47, 0x40, 0x00, 0x10]);
        return directory;
    }

    [Fact]
    public async Task Resume_progress_survives_the_sweep_at_any_age()
    {
        var userId = Guid.NewGuid();
        var assetId = Guid.NewGuid();

        await using (var scope = _provider.CreateAsyncScope())
        {
            var dbContext = scope.ServiceProvider.GetRequiredService<PlaybackDbContext>();
            var progress = PlaybackProgress.Create(userId, assetId, DateTimeOffset.UtcNow.AddYears(-5));
            progress.Record(500, 1000, audioStreamIndex: 1, subtitleStreamIndex: null, DateTimeOffset.UtcNow.AddYears(-5));
            dbContext.Progress.Add(progress);
            await dbContext.SaveChangesAsync();
        }

        var stale = await SeedSessionAsync(finished: true, withTranscodeJob: false);
        await AgeAsync(stale, DateTimeOffset.UtcNow - Window - TimeSpan.FromDays(10));

        await PurgeAsync();
        await PurgeAsync();

        await using (var scope = _provider.CreateAsyncScope())
        {
            var dbContext = scope.ServiceProvider.GetRequiredService<PlaybackDbContext>();
            Assert.False(await dbContext.Sessions.AnyAsync(s => s.Id == stale));

            var progress = await dbContext.Progress
                .AsNoTracking()
                .SingleAsync(p => p.UserId == userId && p.AssetId == assetId);
            Assert.Equal(500, progress.PositionTicks);
        }
    }

    private async Task PurgeAsync()
    {
        await using var scope = _provider.CreateAsyncScope();
        var handler = scope.ServiceProvider.GetRequiredService<ICommandHandler<PurgePlaybackSessionsCommand>>();
        Assert.True((await handler.HandleAsync(new PurgePlaybackSessionsCommand())).IsSuccess);
    }

    private async Task AgeAsync(Guid sessionId, DateTimeOffset updatedAt)
    {
        await using var scope = _provider.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<PlaybackDbContext>();
        await dbContext.Sessions
            .Where(s => s.Id == sessionId)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.UpdatedAt, updatedAt));
    }

    private async Task<Guid> SeedSessionAsync(bool finished, bool withTranscodeJob)
    {
        await using var scope = _provider.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<PlaybackDbContext>();

        var now = DateTimeOffset.UtcNow;
        var session = PlaybackSession.Create(
            Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
            "/library/Movies/Some Movie (2026)/Some Movie (2026).mkv", "matroska",
            new PlaybackPlan(PlaybackMethod.Transcode, [], [], EncoderBackend.Software, [], DecodeAccelerated: false),
            audioStreamIndex: 1, subtitleStreamIndex: null, resumePositionTicks: 0,
            playSessionId: null, now);

        session.Begin(now);
        if (finished)
        {
            session.Stop(now);
        }

        if (withTranscodeJob)
        {
            session.TranscodeJobs.Add(TranscodeJob.Prepare(session.Id, "h264", "aac", hls: true, now));
        }

        dbContext.Sessions.Add(session);
        await dbContext.SaveChangesAsync();
        return session.Id;
    }
}
