using System.Collections.Concurrent;
using Cinomni.Catalog.Contracts;
using Cinomni.Kernel.Identifiers;
using Cinomni.Kernel.Messaging;
using Cinomni.Kernel.Results;
using Cinomni.Kernel.Security;
using Cinomni.Library.Contracts;
using Cinomni.Operations.Messaging;
using Cinomni.Playback.Application;
using Cinomni.Playback.Contracts;
using Cinomni.Playback.Encoding;
using Cinomni.Playback.Persistence;
using Cinomni.Playback.Planning;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Cinomni.Playback.Tests;

/// <summary>
/// The life of a transcode against a real PostgreSQL instance and a real output directory: a slot is
/// admitted or refused with a reason, every way a session ends stops its FFmpeg and reclaims its
/// output, a stream nobody asks for is closed by the sweep, how FFmpeg ended is recorded on the job,
/// and whatever a previous run left behind is adopted or reclaimed. FFmpeg itself is the fake encoder;
/// what it hands back is a process the test can see being stopped.
/// </summary>
public sealed class TranscodeLifecycleTests : IAsyncLifetime, IDisposable
{
    private const string MoviePath = "/data/library/Lifecycle.2024/Lifecycle.2024.mkv";

    private static readonly TimeSpan IdleTimeout = TimeSpan.FromMinutes(5);

    private readonly FakeMediaEncoder _encoder = new() { WritesOutput = true };
    private readonly ManualClock _clock = new(DateTimeOffset.UtcNow);
    private readonly FailureSink _failures = new();
    private readonly CompletedSink _completed = new();
    private readonly LogLines _logs = new();

    private readonly string _transcodeRoot = Path.Combine(
        Path.GetTempPath(), "cinomni-transcode-lifecycle", Guid.NewGuid().ToString("N"));

    private ServiceProvider _provider = null!;

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(_transcodeRoot);
        _provider = await PlaybackTestHost.CreateAsync("cinomni_test_playback_lifecycle", _encoder, services =>
        {
            services.AddSingleton(new PlaybackOptions
            {
                TranscodeRoot = _transcodeRoot,
                MaxConcurrentTranscodes = 3,
                MaxTranscodesPerAccount = 2,
                TranscodeIdleTimeout = IdleTimeout,
            });
            services.AddSingleton<TimeProvider>(_clock);
            // The binary a crashed host's orphan would be: the shell these tests stand in for FFmpeg with.
            services.AddSingleton(new FfmpegEncoderOptions { BinaryPath = TestShell.Binary });
            services.AddSingleton(_failures);
            services.AddScoped<IEventHandler<TranscodeFailed>, FailureSinkHandler>();
            services.AddSingleton(_completed);
            services.AddScoped<IEventHandler<PlaybackCompleted>, CompletedSinkHandler>();
            services.AddSingleton<ILoggerProvider>(_logs);
        });
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
    public async Task Stopping_a_transcode_stops_ffmpeg_and_reclaims_its_output()
    {
        var viewer = Guid.NewGuid();
        var ticket = await TranscodeAsync(viewer);
        var process = Assert.Single(_encoder.Processes);
        Assert.True(Directory.Exists(OutputOf(ticket)));

        await StopAsync(viewer, ticket.SessionId);

        Assert.True(process.Stopped);
        Assert.False(Directory.Exists(OutputOf(ticket)));
        var session = await SessionAsync(ticket.SessionId);
        Assert.Equal(PlaybackState.Completed, session.State);
        Assert.Equal(PlaybackEndReason.Stopped, session.EndReason);
        Assert.Equal(TranscodeState.Cleaned, Assert.Single(session.TranscodeJobs).State);
        Assert.Equal(0, Transcodes.Count);
    }

    [Fact]
    public async Task A_member_past_their_limit_is_refused_with_the_reason_and_nothing_starts()
    {
        var member = Guid.NewGuid();
        await TranscodeAsync(member);
        await TranscodeAsync(member);

        var refused = await RequestAsync(member, TranscodingClient);

        Assert.True(refused.IsFailure);
        Assert.Equal(PlaybackErrors.TranscodeLimit, refused.Error.Code);
        Assert.Contains("your account", refused.Error.Message, StringComparison.Ordinal);
        Assert.Equal(2, _encoder.Requests.Count);
        Assert.Equal(2, await CountSessionsAsync());

        // The limit is on conversions, not on watching: a file this client plays as-is still plays.
        var direct = await RequestAsync(member, DirectPlayClient);
        Assert.True(direct.IsSuccess);
        Assert.Equal(PlaybackMethod.DirectPlay, direct.Value.Method);
    }

    [Fact]
    public async Task A_full_node_refuses_everyone_until_a_stream_ends()
    {
        var first = Guid.NewGuid();
        var firstTicket = await TranscodeAsync(first);
        await TranscodeAsync(Guid.NewGuid());
        await TranscodeAsync(Guid.NewGuid());

        var refused = await RequestAsync(Guid.NewGuid(), TranscodingClient);
        Assert.Equal(PlaybackErrors.TranscodeLimit, refused.Error.Code);
        Assert.Contains("The server", refused.Error.Message, StringComparison.Ordinal);

        await StopAsync(first, firstTicket.SessionId);

        Assert.True((await RequestAsync(Guid.NewGuid(), TranscodingClient)).IsSuccess);
    }

    /// <summary>
    /// The watched threshold completes the session at 90 %, while the end of the film and its credits
    /// are still streaming. Only the viewer leaving — or going silent — may take the stream away.
    /// </summary>
    [Fact]
    public async Task Crossing_the_watched_threshold_does_not_cut_off_the_end_of_the_film()
    {
        var viewer = Guid.NewGuid();
        var ticket = await TranscodeAsync(viewer);
        var process = Assert.Single(_encoder.Processes);

        await ReportAsync(viewer, ticket.SessionId, positionTicks: 950);

        Assert.Equal(PlaybackState.Completed, (await SessionAsync(ticket.SessionId)).State);
        Assert.False(process.Stopped);
        Assert.True(Directory.Exists(OutputOf(ticket)));

        await StopAsync(viewer, ticket.SessionId);

        Assert.True(process.Stopped);
        Assert.False(Directory.Exists(OutputOf(ticket)));
        var session = await SessionAsync(ticket.SessionId);
        Assert.Equal(TranscodeState.Cleaned, Assert.Single(session.TranscodeJobs).State);
        // It ended when it was watched; the stop that came after does not rewrite why.
        Assert.Equal(PlaybackEndReason.Watched, session.EndReason);
    }

    [Fact]
    public async Task A_transcode_that_cannot_start_gives_its_slot_back_and_leaves_no_output()
    {
        _encoder.Fail = true;

        var failed = await RequestAsync(Guid.NewGuid(), TranscodingClient);

        Assert.Equal(PlaybackErrors.TranscodeFailed, failed.Error.Code);
        Assert.Equal(0, Transcodes.Count);
        Assert.Equal(PlaybackEndReason.TranscodeFailed, (await SessionAsync(Assert.Single(await SessionIdsAsync()))).EndReason);
        var request = Assert.Single(_encoder.Requests);
        Assert.False(Directory.Exists(request.OutputDirectory));
        // The failed attempt is still history: the row explains why nothing played.
        Assert.Equal(1, await CountSessionsAsync());
    }

    [Fact]
    public async Task A_request_that_goes_away_while_ffmpeg_starts_gives_its_slot_back()
    {
        _encoder.Cancel = true;

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => RequestAsync(Guid.NewGuid(), TranscodingClient));

        Assert.Equal(0, Transcodes.Count);
        Assert.False(Directory.Exists(Assert.Single(_encoder.Requests).OutputDirectory));
        Assert.Equal(0, await CountSessionsAsync());
    }

    [Fact]
    public async Task A_stream_nobody_asks_for_is_closed_by_the_sweep_and_one_being_watched_is_not()
    {
        var watcher = Guid.NewGuid();
        var leaver = Guid.NewGuid();
        var watched = await TranscodeAsync(watcher);
        var abandoned = await TranscodeAsync(leaver);

        _clock.Advance(IdleTimeout - TimeSpan.FromMinutes(1));
        await ReportAsync(watcher, watched.SessionId, positionTicks: 400, isPaused: true);
        _clock.Advance(TimeSpan.FromMinutes(2));

        await SweepAsync();

        Assert.True(_encoder.Processes[1].Stopped);
        Assert.False(Directory.Exists(OutputOf(abandoned)));
        var closed = await SessionAsync(abandoned.SessionId);
        Assert.Equal(PlaybackState.Completed, closed.State);
        Assert.Equal(PlaybackEndReason.Idle, closed.EndReason);
        Assert.Equal(TranscodeState.Cleaned, Assert.Single(closed.TranscodeJobs).State);

        // Paused, but still there: a paused player keeps reporting.
        Assert.False(_encoder.Processes[0].Stopped);
        Assert.True(Directory.Exists(OutputOf(watched)));
        Assert.Equal(PlaybackState.Paused, (await SessionAsync(watched.SessionId)).State);

        await DrainOutboxAsync();
        Assert.Equal(abandoned.SessionId.Value, Assert.Single(_completed.Sessions));
    }

    [Fact]
    public async Task Serving_a_segment_counts_as_watching()
    {
        var viewer = Guid.NewGuid();
        var ticket = await TranscodeAsync(viewer);

        _clock.Advance(IdleTimeout - TimeSpan.FromMinutes(1));
        await using (var scope = _provider.CreateAsyncScope())
        {
            var served = await scope.ServiceProvider.GetRequiredService<PlaybackStreamer>()
                .ResolveHlsFileAsync(Watcher(viewer), ticket.SessionId, "seg_000.ts");
            Assert.NotNull(served);
        }

        _clock.Advance(TimeSpan.FromMinutes(2));
        await SweepAsync();

        Assert.False(Assert.Single(_encoder.Processes).Stopped);
    }

    [Fact]
    public async Task How_ffmpeg_ended_is_recorded_once_and_a_failure_part_way_ends_the_stream()
    {
        var finished = await TranscodeAsync(Guid.NewGuid());
        var broken = await TranscodeAsync(Guid.NewGuid());
        _encoder.Processes[0].Exit(0);
        _encoder.Processes[1].Exit(1, "/data/library/Private.Title.mkv: Invalid data found when processing input");

        await SweepAsync();
        await SweepAsync();

        // A finished file is still being watched: it keeps its output, its slot and its open session.
        var done = await SessionAsync(finished.SessionId);
        Assert.Equal(TranscodeState.Completed, Assert.Single(done.TranscodeJobs).State);
        Assert.Equal(PlaybackState.Transcoding, done.State);
        Assert.True(Directory.Exists(OutputOf(finished)));
        Assert.True(Transcodes.IsTracked(finished.SessionId.Value));

        // A playlist that will never be finished would keep a player buffering for ever: the session
        // fails, and the output and the slot go.
        var failedSession = await SessionAsync(broken.SessionId);
        Assert.Equal(PlaybackState.Failed, failedSession.State);
        Assert.Equal(PlaybackEndReason.TranscodeFailed, failedSession.EndReason);
        var failedJob = Assert.Single(failedSession.TranscodeJobs);
        Assert.Equal(TranscodeState.Failed, failedJob.State);
        Assert.Equal(
            "FFmpeg exited with code 1. /data/library/Private.Title.mkv: Invalid data found when processing input",
            failedJob.LastError);
        Assert.False(Directory.Exists(OutputOf(broken)));
        Assert.False(Transcodes.IsTracked(broken.SessionId.Value));

        // The event says what happened, not what FFmpeg said: that can quote the media path.
        await DrainOutboxAsync();
        var failure = Assert.Single(_failures.Events);
        Assert.Equal(failedJob.Id, failure.TranscodeJobId);
        Assert.Equal("FFmpeg exited with code 1.", failure.Reason);
    }

    /// <summary>
    /// The player sends its last progress report and its stop together when it unmounts, and the report
    /// usually commits first. A close that lost that race used to leave the row open for good.
    /// </summary>
    [Fact]
    public async Task A_stop_that_loses_the_race_to_the_last_progress_report_still_closes_the_session()
    {
        var viewer = Guid.NewGuid();
        var ticket = await TranscodeAsync(viewer);
        var attempts = 0;

        await using (var scope = _provider.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<SessionUpdates>().ApplyAsync(ticket.SessionId.Value, session =>
            {
                if (++attempts == 1)
                {
                    // The report lands between this close's read and its save. Run off the test's context,
                    // on its own scope and connection, exactly as the concurrent request would.
                    Task.Run(() => ReportAsync(viewer, ticket.SessionId, positionTicks: 300)).GetAwaiter().GetResult();
                }

                return SessionCloser.Close(session, PlaybackEndReason.Stopped);
            });
        }

        Assert.Equal(2, attempts);
        var session = await SessionAsync(ticket.SessionId);
        Assert.Equal(PlaybackState.Completed, session.State);
        Assert.Equal(300, session.PositionTicks);
        Assert.Equal(TranscodeState.Cleaned, Assert.Single(session.TranscodeJobs).State);
    }

    [Fact]
    public async Task An_open_session_whose_close_never_saved_is_closed_by_the_next_sweep()
    {
        var ticket = await TranscodeAsync(Guid.NewGuid());
        // The process is stopped and the output gone, and then the host dies before the row is saved.
        await Transcodes.EndAsync(ticket.SessionId.Value);

        await SweepAsync();

        var session = await SessionAsync(ticket.SessionId);
        Assert.Equal(PlaybackState.Completed, session.State);
        Assert.Equal(PlaybackEndReason.Recovered, session.EndReason);
        Assert.Equal(TranscodeState.Cleaned, Assert.Single(session.TranscodeJobs).State);
        await DrainOutboxAsync();
        Assert.Contains(ticket.SessionId.Value, _completed.Sessions);
    }

    [Fact]
    public async Task Another_account_can_neither_keep_a_stream_alive_nor_stop_it()
    {
        var owner = Guid.NewGuid();
        var stranger = Guid.NewGuid();
        var ticket = await TranscodeAsync(owner);
        var process = Assert.Single(_encoder.Processes);

        await StopAsync(stranger, ticket.SessionId);
        Assert.False(process.Stopped);

        _clock.Advance(IdleTimeout - TimeSpan.FromMinutes(1));
        await ReportAsync(stranger, ticket.SessionId, positionTicks: 100);
        await using (var scope = _provider.CreateAsyncScope())
        {
            Assert.Null(await scope.ServiceProvider.GetRequiredService<PlaybackStreamer>()
                .ResolveHlsFileAsync(Watcher(stranger), ticket.SessionId, "seg_000.ts"));
        }

        _clock.Advance(TimeSpan.FromMinutes(2));
        await SweepAsync();

        // Only its owner's requests count: the stranger's kept nothing alive.
        Assert.True(process.Stopped);
        Assert.Equal(PlaybackState.Completed, (await SessionAsync(ticket.SessionId)).State);
    }

    [Fact]
    public async Task After_a_restart_an_unfinished_transcode_fails_its_session_and_its_output_is_reclaimed()
    {
        var sessionId = await SeedPreviousRunSessionAsync(TranscodeState.Running);

        await SweepAsync();

        var session = await SessionAsync(sessionId);
        Assert.Equal(PlaybackState.Failed, session.State);
        Assert.Equal(PlaybackEndReason.ServerRestarted, session.EndReason);
        var job = Assert.Single(session.TranscodeJobs);
        Assert.Equal(TranscodeState.Failed, job.State);
        Assert.Equal(TranscodeSweep.RestartReason, job.LastError);
        Assert.False(Directory.Exists(OutputOf(sessionId)));
        Assert.False(Transcodes.IsTracked(sessionId));
    }

    [Fact]
    public async Task After_a_restart_a_finished_transcode_is_adopted_and_still_goes_idle()
    {
        var sessionId = await SeedPreviousRunSessionAsync(TranscodeState.Completed);

        await SweepAsync();

        // Somebody may be halfway through the film: the stream carries on from the same files.
        Assert.True(Transcodes.IsTracked(sessionId));
        Assert.True(Directory.Exists(OutputOf(sessionId)));
        Assert.Equal(PlaybackState.Transcoding, (await SessionAsync(sessionId)).State);

        _clock.Advance(IdleTimeout + TimeSpan.FromMinutes(1));
        await SweepAsync();

        Assert.False(Directory.Exists(OutputOf(sessionId)));
        var session = await SessionAsync(sessionId);
        Assert.Equal(PlaybackState.Completed, session.State);
        Assert.Equal(PlaybackEndReason.Idle, session.EndReason);
        Assert.Equal(TranscodeState.Cleaned, Assert.Single(session.TranscodeJobs).State);
    }

    [Fact]
    public async Task After_a_restart_a_session_whose_output_is_already_gone_is_still_failed()
    {
        var sessionId = await SeedPreviousRunSessionAsync(TranscodeState.Running, withOutput: false);

        await SweepAsync();

        var session = await SessionAsync(sessionId);
        Assert.Equal(PlaybackState.Failed, session.State);
        Assert.Equal(TranscodeSweep.RestartReason, Assert.Single(session.TranscodeJobs).LastError);
        await DrainOutboxAsync();
        Assert.Equal(TranscodeSweep.RestartReason, Assert.Single(_failures.Events).Reason);
    }

    [Fact]
    public async Task After_a_restart_a_session_the_watched_threshold_had_completed_only_loses_its_output()
    {
        var sessionId = await SeedPreviousRunSessionAsync(TranscodeState.Running, watched: true);

        await SweepAsync();

        var session = await SessionAsync(sessionId);
        Assert.Equal(PlaybackState.Completed, session.State);
        Assert.Equal(TranscodeState.Cleaned, Assert.Single(session.TranscodeJobs).State);
        Assert.False(Directory.Exists(OutputOf(sessionId)));
    }

    [Fact]
    public async Task A_directory_no_session_names_is_removed_only_when_it_holds_transcode_output()
    {
        var foreign = Guid.NewGuid();
        Directory.CreateDirectory(OutputOf(foreign));
        await File.WriteAllTextAsync(Path.Combine(OutputOf(foreign), "Some.Release.2024.mkv"), "not ours");
        var ours = Guid.NewGuid();
        Directory.CreateDirectory(OutputOf(ours));
        await File.WriteAllTextAsync(Path.Combine(OutputOf(ours), "manifest.m3u8"), "#EXTM3U\n");
        await File.WriteAllTextAsync(Path.Combine(OutputOf(ours), "seg_012.ts"), "segment");

        await SweepAsync();

        // A GUID-named directory under a misconfigured root may be anybody's: only the unmistakable goes.
        Assert.True(File.Exists(Path.Combine(OutputOf(foreign), "Some.Release.2024.mkv")));
        Assert.False(Directory.Exists(OutputOf(ours)));
    }

    [SymbolicLinkFact]
    public async Task A_session_directory_that_is_a_link_is_never_followed()
    {
        var elsewhere = Path.Combine(Path.GetTempPath(), "cinomni-transcode-link-target", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(elsewhere);
        try
        {
            var precious = Path.Combine(elsewhere, "manifest.m3u8");
            await File.WriteAllTextAsync(precious, "somebody else's");
            Directory.CreateSymbolicLink(OutputOf(Guid.NewGuid()), elsewhere);

            await SweepAsync();

            Assert.True(File.Exists(precious));
        }
        finally
        {
            Directory.Delete(elsewhere, recursive: true);
        }
    }

    [Fact]
    public async Task What_ffmpeg_said_when_it_would_not_start_stays_out_of_the_log_and_the_event()
    {
        _encoder.Failure = new TranscodeStartFailedException(
            1, "/data/library/Private.Title (2024)/Private.Title.mkv: Invalid data found when processing input");

        var failed = await RequestAsync(Guid.NewGuid(), TranscodingClient);

        Assert.Equal(PlaybackErrors.TranscodeFailed, failed.Error.Code);
        var job = Assert.Single((await SessionAsync(Assert.Single(await SessionIdsAsync()))).TranscodeJobs);
        Assert.Contains("Private.Title", job.LastError, StringComparison.Ordinal);
        Assert.DoesNotContain(_logs.Lines, line => line.Contains("Private.Title", StringComparison.Ordinal));
        await DrainOutboxAsync();
        Assert.Equal("FFmpeg exited with code 1 before writing a manifest.", Assert.Single(_failures.Events).Reason);
    }

    [Fact]
    public async Task A_transcode_that_finishes_starting_while_the_host_stops_is_stopped_not_orphaned()
    {
        _encoder.OnStart = () => Transcodes.StopAllAsync();

        var failed = await RequestAsync(Guid.NewGuid(), TranscodingClient);

        Assert.Equal(PlaybackErrors.TranscodeFailed, failed.Error.Code);
        Assert.True(Assert.Single(_encoder.Processes).Stopped);
        Assert.Equal(0, Transcodes.Count);

        _encoder.OnStart = null;
        Assert.Equal(PlaybackErrors.TranscodeLimit, (await RequestAsync(Guid.NewGuid(), TranscodingClient)).Error.Code);
    }

    /// <summary>
    /// Losing access used to stop the segments and nothing else: a player that kept reporting kept its
    /// stream, its FFmpeg and its slot. The next report now closes it.
    /// </summary>
    [Fact]
    public async Task Losing_access_closes_the_stream_at_the_next_progress_report()
    {
        var member = Guid.NewGuid();
        CollectionId shelf;
        await using (var scope = _provider.CreateAsyncScope())
        {
            var collections = scope.ServiceProvider.GetRequiredService<ICollectionAdministration>();
            shelf = (await collections.CreateAsync("Grown-ups", CollectionKind.Movies, CollectionAccessMode.Restricted)).Value;
            await collections.GrantAsync(shelf, member, Guid.NewGuid());
        }

        var assetId = await RegisterAssetAsync(shelf);
        var started = await RequestAsync(member, TranscodingClient, assetId);
        Assert.True(started.IsSuccess, started.IsFailure ? started.Error.Message : null);
        var ticket = started.Value;
        var process = Assert.Single(_encoder.Processes);

        await using (var scope = _provider.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<ICollectionAdministration>().RevokeAsync(shelf, member);
        }

        await ReportAsync(member, ticket.SessionId, positionTicks: 200);

        Assert.True(process.Stopped);
        Assert.False(Directory.Exists(OutputOf(ticket)));
        Assert.Equal(0, Transcodes.Count);
        var session = await SessionAsync(ticket.SessionId);
        Assert.Equal(PlaybackState.Completed, session.State);
        Assert.Equal(PlaybackEndReason.AccessRevoked, session.EndReason);
        // Nothing about the title was recorded for somebody who may no longer see it.
        Assert.Equal(0, session.PositionTicks);
    }

    [Fact]
    public async Task A_stream_is_closed_once_it_reaches_its_lifetime_however_busy_its_player_keeps_it()
    {
        var viewer = Guid.NewGuid();
        var ticket = await TranscodeAsync(viewer);

        _clock.Advance(new PlaybackOptions().MaxTranscodeLifetime + TimeSpan.FromMinutes(1));
        await ReportAsync(viewer, ticket.SessionId, positionTicks: 500);
        await SweepAsync();

        Assert.True(Assert.Single(_encoder.Processes).Stopped);
        var session = await SessionAsync(ticket.SessionId);
        Assert.Equal(PlaybackState.Completed, session.State);
        Assert.Equal(PlaybackEndReason.Expired, session.EndReason);
        Assert.Equal(0, Transcodes.Count);
    }

    /// <summary>
    /// Outside a container a crashed host leaves its FFmpeg running with nothing to stop it. The job row
    /// remembers which process it was, and the next start stops exactly that one.
    /// </summary>
    [Fact]
    public async Task After_a_restart_ffmpeg_left_running_by_the_crashed_host_is_stopped()
    {
        var sessionId = await SeedPreviousRunSessionAsync(TranscodeState.Running);
        // Named on its own command line the way FFmpeg names its manifest, and recorded on the job.
        await using var orphan = FfmpegProcess.Start(
            TestShell.LongRunningNaming(Path.Combine(OutputOf(sessionId), "manifest.m3u8")));
        await RecordProcessAsync(sessionId, orphan);

        await SweepAsync();

        Assert.True(orphan.HasExited || await ExitsSoonAsync(orphan));
        Assert.Equal(PlaybackEndReason.ServerRestarted, (await SessionAsync(sessionId)).EndReason);
    }

    [Fact]
    public async Task A_stranger_without_access_reporting_on_a_stream_changes_nothing()
    {
        var owner = Guid.NewGuid();
        CollectionId shelf;
        await using (var scope = _provider.CreateAsyncScope())
        {
            var collections = scope.ServiceProvider.GetRequiredService<ICollectionAdministration>();
            shelf = (await collections.CreateAsync("Grown-ups", CollectionKind.Movies, CollectionAccessMode.Restricted)).Value;
            await collections.GrantAsync(shelf, owner, Guid.NewGuid());
        }

        var started = await RequestAsync(owner, TranscodingClient, await RegisterAssetAsync(shelf));
        var ticket = started.Value;

        // Not the owner — and not allowed to see the title either. Ownership is checked first, so the
        // stranger's missing access can never be what closes the owner's stream.
        await ReportAsync(Guid.NewGuid(), ticket.SessionId, positionTicks: 700);

        Assert.False(Assert.Single(_encoder.Processes).Stopped);
        var session = await SessionAsync(ticket.SessionId);
        Assert.Equal(PlaybackState.Transcoding, session.State);
        Assert.Null(session.EndReason);
    }

    [Fact]
    public async Task A_session_that_names_no_title_is_closed_at_its_next_report()
    {
        var viewer = Guid.NewGuid();
        var ticket = await TranscodeAsync(viewer);
        await using (var scope = _provider.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<PlaybackDbContext>().Sessions
                .Where(s => s.Id == ticket.SessionId.Value)
                .ExecuteUpdateAsync(s => s.SetProperty(x => x.WorkId, (Guid?)null));
        }

        await ReportAsync(viewer, ticket.SessionId, positionTicks: 100);

        // Nothing to check access against, so it fails closed — as the streamer serves it nothing.
        Assert.True(Assert.Single(_encoder.Processes).Stopped);
        Assert.Equal(PlaybackEndReason.AccessRevoked, (await SessionAsync(ticket.SessionId)).EndReason);
    }

    [Fact]
    public async Task A_stop_the_sweep_runs_into_keeps_its_own_reason()
    {
        var viewer = Guid.NewGuid();
        var ticket = await TranscodeAsync(viewer);

        // The sweep passes while the stop is between stopping the process and saving the row: it must
        // leave the session to the close in hand, not reconcile it as recovered.
        await Transcodes.EndForCloseAsync(ticket.SessionId.Value, async () =>
        {
            await SweepAsync();
            await using var scope = _provider.CreateAsyncScope();
            await scope.ServiceProvider.GetRequiredService<SessionUpdates>().ApplyAsync(
                ticket.SessionId.Value, session => SessionCloser.Close(session, PlaybackEndReason.Stopped));
        });

        Assert.Equal(PlaybackEndReason.Stopped, (await SessionAsync(ticket.SessionId)).EndReason);
    }

    [Fact]
    public async Task Output_nothing_owns_is_reclaimed_and_nothing_else_under_the_root_is_touched()
    {
        var orphan = Guid.NewGuid();
        Directory.CreateDirectory(OutputOf(orphan));
        var unrelated = Path.Combine(_transcodeRoot, "not-a-session");
        Directory.CreateDirectory(unrelated);
        var looseFile = Path.Combine(_transcodeRoot, Guid.NewGuid().ToString("D"));
        await File.WriteAllTextAsync(looseFile, "not a directory");
        var running = await TranscodeAsync(Guid.NewGuid());

        await SweepAsync();

        Assert.False(Directory.Exists(OutputOf(orphan)));
        Assert.True(Directory.Exists(unrelated));
        Assert.True(File.Exists(looseFile));
        Assert.True(Directory.Exists(OutputOf(running)));
    }

    // -- helpers ---------------------------------------------------------------------------------

    private static readonly ClientCapability TranscodingClient = new(["matroska"], ["vp9"], ["aac"], MaxHeight: null);

    private static readonly ClientCapability DirectPlayClient = new(["matroska"], ["h264"], ["aac"], MaxHeight: null);

    private ActiveTranscodes Transcodes => _provider.GetRequiredService<ActiveTranscodes>();

    private string OutputOf(PlaybackTicket ticket) => OutputOf(ticket.SessionId.Value);

    private string OutputOf(Guid sessionId) => Path.Combine(_transcodeRoot, sessionId.ToString("D"));

    private static Viewer Watcher(Guid userId) => new(userId, IsAdministrator: false);

    private async Task<PlaybackTicket> TranscodeAsync(Guid userId)
    {
        var result = await RequestAsync(userId, TranscodingClient);
        Assert.True(result.IsSuccess, result.IsFailure ? result.Error.Message : null);
        Assert.Equal(PlaybackMethod.Transcode, result.Value.Method);
        return result.Value;
    }

    private async Task<Result<PlaybackTicket>> RequestAsync(Guid userId, ClientCapability capability, Guid? asset = null)
    {
        var assetId = asset ?? await RegisterAssetAsync();
        await using var scope = _provider.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<IPlaybackSessionCommands>()
            .RequestPlaybackAsync(Watcher(userId), assetId, capability);
    }

    private async Task StopAsync(Guid userId, PlaybackSessionId sessionId)
    {
        await using var scope = _provider.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<IPlaybackSessionCommands>().StopPlaybackAsync(userId, sessionId);
    }

    private async Task ReportAsync(Guid userId, PlaybackSessionId sessionId, long positionTicks, bool isPaused = false)
    {
        await using var scope = _provider.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<IPlaybackSessionCommands>()
            .ReportProgressAsync(Watcher(userId), sessionId, positionTicks, durationTicks: 1000, isPaused);
    }

    private async Task SweepAsync()
    {
        await using var scope = _provider.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<TranscodeSweep>().RunAsync();
    }

    private Task<PlaybackSession> SessionAsync(PlaybackSessionId sessionId) => SessionAsync(sessionId.Value);

    private async Task<PlaybackSession> SessionAsync(Guid sessionId)
    {
        await using var scope = _provider.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<PlaybackDbContext>().Sessions
            .AsNoTracking()
            .Include(s => s.TranscodeJobs)
            .SingleAsync(s => s.Id == sessionId);
    }

    private async Task<List<Guid>> SessionIdsAsync()
    {
        await using var scope = _provider.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<PlaybackDbContext>().Sessions.Select(s => s.Id).ToListAsync();
    }

    private async Task<int> CountSessionsAsync()
    {
        await using var scope = _provider.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<PlaybackDbContext>().Sessions.CountAsync();
    }

    /// <summary>
    /// A session a previous run of the server opened and never closed, with its output still on disk —
    /// what a restart in the middle of an evening leaves behind.
    /// </summary>
    private async Task<Guid> SeedPreviousRunSessionAsync(TranscodeState jobState, bool withOutput = true, bool watched = false)
    {
        var before = Transcodes.StartedAt - TimeSpan.FromHours(1);
        var session = PlaybackSession.Create(
            Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), MoviePath, "matroska",
            new PlaybackPlan(PlaybackMethod.Transcode, [], [], EncoderBackend.Software, [], DecodeAccelerated: false),
            audioStreamIndex: 1, subtitleStreamIndex: null, resumePositionTicks: 0, playSessionId: null, before);
        session.Begin(before);
        if (watched)
        {
            session.ReportProgress(950, 1000, isPaused: false, before);
        }

        var job = TranscodeJob.Prepare(session.Id, "libx264", "aac", hls: true, before);
        job.MarkRunning(OutputOf(session.Id));
        if (jobState == TranscodeState.Completed)
        {
            job.RecordExit(0, string.Empty);
        }

        session.AddTranscodeJob(job);

        await using (var scope = _provider.CreateAsyncScope())
        {
            var dbContext = scope.ServiceProvider.GetRequiredService<PlaybackDbContext>();
            dbContext.Sessions.Add(session);
            await dbContext.SaveChangesAsync();
        }

        if (withOutput)
        {
            Directory.CreateDirectory(OutputOf(session.Id));
            await File.WriteAllTextAsync(Path.Combine(OutputOf(session.Id), "manifest.m3u8"), "#EXTM3U\n");
        }

        return session.Id;
    }

    private async Task<Guid> RegisterAssetAsync(CollectionId? collection = null)
    {
        Guid workId;
        await using (var scope = _provider.CreateAsyncScope())
        {
            var added = await scope.ServiceProvider.GetRequiredService<ICatalogCommands>()
                .AddMovieAsync($"Lifecycle {Guid.NewGuid():N}", 2024, [], collection);
            workId = added.Value.Value;
        }

        var assetId = Uuid7.New();
        var request = new RegisterMediaAssetRequest(
            assetId,
            WorkId: workId,
            TargetIds: [Uuid7.New()],
            // One file per asset: a library path belongs to one version.
            FullPath: $"/data/library/Lifecycle/{assetId:N}.mkv",
            Size: 2_000_000_000,
            Container: "matroska",
            Streams:
            [
                new MediaStreamInput(0, MediaStreamType.Video, "h264", null, null, 1920, 1080, null, null, true, false),
                new MediaStreamInput(1, MediaStreamType.Audio, "aac", "eng", 6, null, null, null, null, true, false),
            ]);

        await using (var scope = _provider.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<ILibraryCommands>().RegisterMediaAssetAsync(request);
        }

        return assetId;
    }

    /// <summary>Records on a seeded job which process was converting it, as the crashed run would have.</summary>
    private async Task RecordProcessAsync(Guid sessionId, FfmpegProcess process)
    {
        await using var scope = _provider.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<PlaybackDbContext>().TranscodeJobs
            .Where(job => job.SessionId == sessionId)
            .ExecuteUpdateAsync(job => job
                .SetProperty(x => x.ProcessId, process.ProcessId)
                .SetProperty(x => x.ProcessStartedAt, process.StartedAt));
    }

    /// <summary>The process was killed from outside its wrapper; give the exit a moment to be observed.</summary>
    private static async Task<bool> ExitsSoonAsync(FfmpegProcess process)
    {
        for (var i = 0; i < 100 && !process.HasExited; i++)
        {
            await Task.Delay(50);
        }

        return process.HasExited;
    }

    private async Task DrainOutboxAsync()
    {
        await using var scope = _provider.CreateAsyncScope();
        var relay = scope.ServiceProvider.GetRequiredService<OutboxRelay>();
        while (await relay.ProcessBatchAsync() > 0)
        {
        }
    }

    private sealed class FailureSink
    {
        public ConcurrentQueue<TranscodeFailed> Events { get; } = [];
    }

    private sealed class FailureSinkHandler(FailureSink sink) : IEventHandler<TranscodeFailed>
    {
        public Task HandleAsync(TranscodeFailed domainEvent, CancellationToken cancellationToken = default)
        {
            sink.Events.Enqueue(domainEvent);
            return Task.CompletedTask;
        }
    }

    private sealed class CompletedSink
    {
        public ConcurrentQueue<Guid> Sessions { get; } = [];
    }

    private sealed class CompletedSinkHandler(CompletedSink sink) : IEventHandler<PlaybackCompleted>
    {
        public Task HandleAsync(PlaybackCompleted domainEvent, CancellationToken cancellationToken = default)
        {
            sink.Sessions.Enqueue(domainEvent.SessionId);
            return Task.CompletedTask;
        }
    }

    /// <summary>Every formatted log line and exception message the module writes, to assert what never reaches one.</summary>
    private sealed class LogLines : ILoggerProvider
    {
        public ConcurrentQueue<string> Lines { get; } = [];

        public ILogger CreateLogger(string categoryName) => new Collector(Lines);

        public void Dispose()
        {
        }

        private sealed class Collector(ConcurrentQueue<string> lines) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state)
                where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(
                LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            {
                lines.Enqueue(formatter(state, exception));
                if (exception is not null)
                {
                    lines.Enqueue(exception.ToString());
                }
            }
        }
    }
}
