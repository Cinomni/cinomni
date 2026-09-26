using Cinomni.Playback.Application;
using Cinomni.Playback.Encoding;
using Microsoft.Extensions.Logging.Abstractions;

namespace Cinomni.Playback.Tests;

/// <summary>
/// The registry of transcodes is what bounds what one node spends on playback and what stops the
/// FFmpeg processes nobody is watching. These pin the limits, the ownership of a process from the
/// moment it is handed over, and the idle clock — against a real directory tree, so reclaiming the
/// output is asserted on disk.
/// </summary>
public sealed class ActiveTranscodesTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "cinomni-active-transcodes", Guid.NewGuid().ToString("N"));
    private readonly ManualClock _clock = new(new DateTimeOffset(2026, 9, 23, 20, 0, 0, TimeSpan.Zero));
    private readonly PlaybackOptions _options;
    private readonly ActiveTranscodes _transcodes;

    public ActiveTranscodesTests()
    {
        Directory.CreateDirectory(_root);
        _options = new PlaybackOptions
        {
            TranscodeRoot = _root,
            MaxConcurrentTranscodes = 3,
            MaxTranscodesPerAccount = 2,
            TranscodeIdleTimeout = TimeSpan.FromMinutes(5),
        };
        _transcodes = new ActiveTranscodes(
            _options,
            new TranscodeWorkspace(_options, NullLogger<TranscodeWorkspace>.Instance),
            _clock,
            NullLogger<ActiveTranscodes>.Instance);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Fact]
    public void One_account_cannot_take_more_than_its_share_of_the_node()
    {
        var member = Guid.NewGuid();

        Assert.Equal(TranscodeAdmission.Admitted, _transcodes.TryAdmit(Guid.NewGuid(), member));
        Assert.Equal(TranscodeAdmission.Admitted, _transcodes.TryAdmit(Guid.NewGuid(), member));
        Assert.Equal(TranscodeAdmission.AccountFull, _transcodes.TryAdmit(Guid.NewGuid(), member));

        // Somebody else in the household still gets the slot that is left.
        Assert.Equal(TranscodeAdmission.Admitted, _transcodes.TryAdmit(Guid.NewGuid(), Guid.NewGuid()));
    }

    [Fact]
    public void A_full_node_refuses_everyone_and_a_refusal_holds_nothing()
    {
        for (var i = 0; i < _options.MaxConcurrentTranscodes; i++)
        {
            Assert.Equal(TranscodeAdmission.Admitted, _transcodes.TryAdmit(Guid.NewGuid(), Guid.NewGuid()));
        }

        var refused = Guid.NewGuid();
        Assert.Equal(TranscodeAdmission.NodeFull, _transcodes.TryAdmit(refused, Guid.NewGuid()));

        Assert.False(_transcodes.IsTracked(refused));
        Assert.Equal(_options.MaxConcurrentTranscodes, _transcodes.Count);
    }

    [Fact]
    public async Task Racing_requests_never_admit_more_than_the_node_allows()
    {
        var admitted = await Task.WhenAll(Enumerable.Range(0, 50)
            .Select(_ => Task.Run(() => _transcodes.TryAdmit(Guid.NewGuid(), Guid.NewGuid()))));

        Assert.Equal(_options.MaxConcurrentTranscodes, admitted.Count(a => a == TranscodeAdmission.Admitted));
    }

    [Fact]
    public async Task Ending_a_transcode_stops_its_process_reclaims_its_output_and_frees_its_slot()
    {
        var member = Guid.NewGuid();
        var session = Guid.NewGuid();
        var process = new FakeRunningTranscode();
        _transcodes.TryAdmit(session, member);
        Assert.True(_transcodes.Attach(session, process));
        var output = SeedOutput(session);

        await _transcodes.EndAsync(session);

        Assert.True(process.Stopped);
        Assert.False(Directory.Exists(output));
        Assert.False(_transcodes.IsTracked(session));

        // Idempotent, and the slot really is free again.
        await _transcodes.EndAsync(session);
        Assert.Equal(TranscodeAdmission.Admitted, _transcodes.TryAdmit(Guid.NewGuid(), member));
        Assert.Equal(TranscodeAdmission.Admitted, _transcodes.TryAdmit(Guid.NewGuid(), member));
    }

    [Fact]
    public async Task A_process_that_arrives_after_its_slot_was_ended_is_refused_so_the_caller_stops_it()
    {
        var session = Guid.NewGuid();
        _transcodes.TryAdmit(session, Guid.NewGuid());
        await _transcodes.EndAsync(session);

        Assert.False(_transcodes.Attach(session, new FakeRunningTranscode()));
        Assert.False(_transcodes.IsTracked(session));
    }

    [Fact]
    public void A_transcode_is_idle_only_once_nothing_has_asked_for_it_for_the_whole_timeout()
    {
        var watched = Guid.NewGuid();
        var abandoned = Guid.NewGuid();
        _transcodes.TryAdmit(watched, Guid.NewGuid());
        _transcodes.TryAdmit(abandoned, Guid.NewGuid());

        _clock.Advance(TimeSpan.FromMinutes(4));
        _transcodes.Touch(watched);
        Assert.Empty(_transcodes.Idle());

        _clock.Advance(TimeSpan.FromMinutes(2));

        Assert.Equal([abandoned], _transcodes.Idle());
    }

    [Fact]
    public void An_exit_is_pending_until_it_is_recorded_and_a_stopped_process_is_never_one()
    {
        var finished = Guid.NewGuid();
        var running = Guid.NewGuid();
        var finishedProcess = new FakeRunningTranscode();
        _transcodes.TryAdmit(finished, Guid.NewGuid());
        _transcodes.TryAdmit(running, Guid.NewGuid());
        _transcodes.Attach(finished, finishedProcess);
        _transcodes.Attach(running, new FakeRunningTranscode());

        finishedProcess.Exit(1, "Error while decoding");

        var exit = Assert.Single(_transcodes.PendingExits());
        Assert.Equal(finished, exit.SessionId);
        Assert.Equal(1, exit.ExitCode);
        Assert.Equal("Error while decoding", exit.Diagnostics);
        // Reading is not recording: an outcome that failed to persist is offered again next time.
        Assert.Single(_transcodes.PendingExits());

        _transcodes.ExitRecorded(finished);
        Assert.Empty(_transcodes.PendingExits());
        // It keeps its slot: the viewer is still watching the file it finished writing.
        Assert.True(_transcodes.IsTracked(finished));
    }

    [Fact]
    public async Task Stopping_everything_stops_every_process_and_leaves_the_output_for_the_next_run()
    {
        var sessions = new[] { Guid.NewGuid(), Guid.NewGuid() };
        var processes = new List<FakeRunningTranscode>();
        foreach (var session in sessions)
        {
            var process = new FakeRunningTranscode();
            processes.Add(process);
            _transcodes.TryAdmit(session, Guid.NewGuid());
            _transcodes.Attach(session, process);
            SeedOutput(session);
        }

        await _transcodes.StopAllAsync();

        Assert.All(processes, process => Assert.True(process.Stopped));
        Assert.Equal(0, _transcodes.Count);
        Assert.All(sessions, session => Assert.True(Directory.Exists(Path.Combine(_root, session.ToString("D")))));
    }

    [Fact]
    public async Task A_viewer_who_came_back_after_the_idle_list_was_taken_keeps_their_stream()
    {
        var session = Guid.NewGuid();
        var process = new FakeRunningTranscode();
        _transcodes.TryAdmit(session, Guid.NewGuid());
        _transcodes.Attach(session, process);
        _clock.Advance(TimeSpan.FromMinutes(6));

        var idle = _transcodes.Idle();
        _transcodes.Touch(session);

        Assert.Equal([session], idle);
        Assert.False(await _transcodes.EndIfIdleAsync(session));
        Assert.False(process.Stopped);
        Assert.True(_transcodes.IsTracked(session));
    }

    [Fact]
    public async Task A_process_that_refuses_to_stop_does_not_keep_the_others_running()
    {
        var stubborn = Guid.NewGuid();
        var ordinary = Guid.NewGuid();
        var ordinaryProcess = new FakeRunningTranscode();
        _transcodes.TryAdmit(stubborn, Guid.NewGuid());
        _transcodes.TryAdmit(ordinary, Guid.NewGuid());
        _transcodes.Attach(stubborn, new StubbornTranscode());
        _transcodes.Attach(ordinary, ordinaryProcess);
        var output = SeedOutput(stubborn);

        await _transcodes.EndAsync(stubborn);
        Assert.False(Directory.Exists(output));

        _transcodes.TryAdmit(stubborn, Guid.NewGuid());
        _transcodes.Attach(stubborn, new StubbornTranscode());
        await _transcodes.StopAllAsync();

        Assert.True(ordinaryProcess.Stopped);
    }

    [Fact]
    public async Task A_host_that_is_stopping_admits_and_attaches_nothing()
    {
        var admitted = Guid.NewGuid();
        _transcodes.TryAdmit(admitted, Guid.NewGuid());

        await _transcodes.StopAllAsync();

        Assert.Equal(TranscodeAdmission.NodeFull, _transcodes.TryAdmit(Guid.NewGuid(), Guid.NewGuid()));
        Assert.False(_transcodes.Attach(admitted, new FakeRunningTranscode()));
    }

    private string SeedOutput(Guid sessionId)
    {
        var directory = Path.Combine(_root, sessionId.ToString("D"));
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "manifest.m3u8"), "#EXTM3U\n");
        return directory;
    }

    /// <summary>A process whose tree could not all be signalled — what <c>Process.Kill</c> reports with an AggregateException.</summary>
    private sealed class StubbornTranscode : IRunningTranscode
    {
        public bool HasExited => false;

        public int? ProcessId => null;

        public DateTimeOffset? StartedAt => null;

        public int? ExitCode => null;

        public string Diagnostics => string.Empty;

        public ValueTask DisposeAsync() =>
            throw new AggregateException(new InvalidOperationException("a child of this process could not be stopped"));
    }
}
