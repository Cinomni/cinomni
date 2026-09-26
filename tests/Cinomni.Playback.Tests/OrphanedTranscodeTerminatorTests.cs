using System.Diagnostics;
using Cinomni.Playback.Encoding;
using Microsoft.Extensions.Logging.Abstractions;

namespace Cinomni.Playback.Tests;

/// <summary>
/// What a crashed host left running is stopped on the next start — and only that. The recorded id and
/// start come from a database row, so a process is stopped only when every fact agrees: the id, the
/// start, the configured binary and — on Linux — its own command line naming the session's output.
/// Real processes: the subject is the operating system's answer.
/// </summary>
public sealed class OrphanedTranscodeTerminatorTests : IDisposable
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(30);

    private readonly string _output = Path.Combine(Path.GetTempPath(), "cinomni-orphan", Guid.NewGuid().ToString("D"));

    private readonly OrphanedTranscodeTerminator _terminator = Terminator(TestShell.Binary);

    public void Dispose()
    {
        if (Directory.Exists(_output))
        {
            Directory.Delete(_output, recursive: true);
        }
    }

    [Fact]
    public async Task The_recorded_process_is_stopped()
    {
        await using var orphan = FfmpegProcess.Start(TestShell.LongRunningNaming(Manifest));

        var outcome = await _terminator.TryTerminateAsync(Id(orphan), Start(orphan), _output);

        Assert.Equal(OrphanOutcome.Stopped, outcome);
        await WaitUntilGoneAsync(Id(orphan), Start(orphan));
    }

    [Fact]
    public async Task A_process_that_took_over_the_id_later_is_left_alone()
    {
        await using var bystander = FfmpegProcess.Start(TestShell.LongRunningNaming(Manifest));

        // The id matches; the start does not — the recorded process was an earlier one.
        var outcome = await _terminator.TryTerminateAsync(Id(bystander), Start(bystander) - TimeSpan.FromMinutes(10), _output);

        Assert.Equal(OrphanOutcome.NotOurs, outcome);
        Assert.False(bystander.HasExited);
    }

    [Fact]
    public async Task A_process_that_is_not_the_configured_encoder_is_left_alone()
    {
        await using var bystander = FfmpegProcess.Start(TestShell.LongRunningNaming(Manifest));

        var outcome = await Terminator("ffmpeg").TryTerminateAsync(Id(bystander), Start(bystander), _output);

        Assert.Equal(OrphanOutcome.NotOurs, outcome);
        Assert.False(bystander.HasExited);
    }

    [Fact]
    public async Task The_host_itself_and_an_impossible_id_are_never_candidates()
    {
        using var self = Process.GetCurrentProcess();

        Assert.Equal(
            OrphanOutcome.NotOurs,
            await _terminator.TryTerminateAsync(Environment.ProcessId, OrphanedTranscodeTerminator.StartOf(self), _output));
        Assert.Equal(OrphanOutcome.NotOurs, await _terminator.TryTerminateAsync(0, DateTimeOffset.UtcNow, _output));
        Assert.Equal(OrphanOutcome.NotOurs, await _terminator.TryTerminateAsync(-1, DateTimeOffset.UtcNow, _output));
    }

    [Fact]
    public async Task An_id_nothing_runs_under_any_more_is_nothing_to_stop()
    {
        var gone = FfmpegProcess.Start(TestShell.Command(windows: "exit 0", unix: "exit 0"));
        var processId = Id(gone);
        var startedAt = Start(gone);
        await gone.DisposeAsync();
        await WaitUntilGoneAsync(processId, startedAt);

        Assert.NotEqual(OrphanOutcome.Stopped, await _terminator.TryTerminateAsync(processId, startedAt, _output));
    }

    [UnixFact]
    public async Task A_process_whose_command_line_names_another_output_is_left_alone()
    {
        await using var bystander = FfmpegProcess.Start(
            TestShell.LongRunningNaming(Path.Combine(Path.GetTempPath(), "somebody-else", "manifest.m3u8")));

        var outcome = await _terminator.TryTerminateAsync(Id(bystander), Start(bystander), _output);

        Assert.Equal(OrphanOutcome.NotOurs, outcome);
        Assert.False(bystander.HasExited);
    }

    /// <summary>
    /// The case the feature exists for: after a crash the orphan is nobody's child any more. A shell
    /// starts it in the background and exits, which is the same thing on a small scale.
    /// </summary>
    [UnixFact]
    public async Task An_orphan_that_is_not_this_process_child_is_stopped_too()
    {
        var script = $"/bin/sh -c 'sleep 120; exit 0' sh '{Manifest}' >/dev/null 2>&1 & echo $! >&2";
        await using var launcher = FfmpegProcess.Start(TestShell.Command(windows: string.Empty, unix: script));
        var deadline = Stopwatch.StartNew();
        while (!launcher.HasExited)
        {
            Assert.True(deadline.Elapsed < Patience, "The launcher never finished.");
            await Task.Delay(50);
        }

        var orphanId = int.Parse(launcher.Diagnostics.Trim(), System.Globalization.CultureInfo.InvariantCulture);
        DateTimeOffset startedAt;
        using (var orphan = Process.GetProcessById(orphanId))
        {
            startedAt = OrphanedTranscodeTerminator.StartOf(orphan);
        }

        Assert.Equal(OrphanOutcome.Stopped, await _terminator.TryTerminateAsync(orphanId, startedAt, _output));
        await WaitUntilGoneAsync(orphanId, startedAt);
    }

    private string Manifest => Path.Combine(_output, "manifest.m3u8");

    private static OrphanedTranscodeTerminator Terminator(string binary) =>
        new(new FfmpegEncoderOptions { BinaryPath = binary }, NullLogger<OrphanedTranscodeTerminator>.Instance);

    private static int Id(FfmpegProcess process) => Assert.NotNull(process.ProcessId);

    private static DateTimeOffset Start(FfmpegProcess process) => Assert.NotNull(process.StartedAt);

    /// <summary>Until nothing runs under the id — or something else does, which started later.</summary>
    private static async Task WaitUntilGoneAsync(int processId, DateTimeOffset startedAt)
    {
        var deadline = Stopwatch.StartNew();
        while (IsRunning(processId, startedAt))
        {
            Assert.True(deadline.Elapsed < Patience, $"Process {processId} is still running.");
            await Task.Delay(50);
        }
    }

    private static bool IsRunning(int processId, DateTimeOffset startedAt)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            return !process.HasExited
                && (OrphanedTranscodeTerminator.StartOf(process) - startedAt).Duration() <= OrphanedTranscodeTerminator.StartTolerance;
        }
        catch (ArgumentException)
        {
            return false;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }
}

/// <summary>A fact about what only Unix exposes. Skipped rather than returning early, which xUnit 2 would report as passed.</summary>
public sealed class UnixFactAttribute : FactAttribute
{
    public UnixFactAttribute()
    {
        if (OperatingSystem.IsWindows())
        {
            Skip = "This needs what only Unix exposes: a process's own command line, or a shell to orphan a child with.";
        }
    }
}
