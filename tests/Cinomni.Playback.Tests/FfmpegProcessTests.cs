using System.Diagnostics;
using Cinomni.Playback.Encoding;

namespace Cinomni.Playback.Tests;

/// <summary>
/// The process wrapper against real child processes — the operating system's own shell standing in
/// for FFmpeg, since what is under test is the pipe and the kill, not the encoder. A pipe nobody reads
/// fills its buffer and blocks the writer, so the first test would hang, not fail slowly, without the
/// drain; the bounded wait turns that hang into a failure.
/// </summary>
public sealed class FfmpegProcessTests
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(30);

    [Fact]
    public async Task A_process_that_floods_stderr_runs_to_the_end_and_only_its_tail_is_kept()
    {
        // About 2 MB of stderr: far past any pipe buffer, on every platform.
        await using var process = FfmpegProcess.Start(Shell(
            windows: "[Console]::Error.Write(('x' * 99 + [char]10) * 20000); [Console]::Error.Write('the last line')",
            unix: "i=0; while [ $i -lt 20000 ]; do echo xxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxx >&2; i=$((i+1)); done; printf 'the last line' >&2"));

        await WaitForExitAsync(process);

        Assert.Equal(0, process.ExitCode);
        Assert.EndsWith("the last line", process.Diagnostics, StringComparison.Ordinal);
        Assert.True(process.Diagnostics.Length <= BoundedLineTail.MaxLines * (BoundedLineTail.MaxLineLength + 1));
    }

    [Fact]
    public async Task Disposing_kills_a_process_that_would_otherwise_run_on_and_is_safe_to_repeat()
    {
        var process = FfmpegProcess.Start(TestShell.LongRunning());
        Assert.False(process.HasExited);

        var stopwatch = Stopwatch.StartNew();
        await process.DisposeAsync();
        await process.DisposeAsync();

        Assert.True(process.HasExited);
        Assert.True(stopwatch.Elapsed < Patience, $"Stopping took {stopwatch.Elapsed}.");
    }

    [Fact]
    public async Task A_process_that_failed_reports_its_code_and_what_it_said()
    {
        await using var process = FfmpegProcess.Start(Shell(
            windows: "[Console]::Error.WriteLine('Invalid data found when processing input'); exit 3",
            unix: "echo 'Invalid data found when processing input' >&2; exit 3"));

        await WaitForExitAsync(process);

        Assert.Equal(3, process.ExitCode);
        Assert.Equal("Invalid data found when processing input", process.Diagnostics);
    }

    [Fact]
    public async Task A_running_process_reports_the_identity_the_operating_system_knows_it_by()
    {
        await using var process = FfmpegProcess.Start(TestShell.LongRunning());

        using var sameProcess = Process.GetProcessById(Assert.NotNull(process.ProcessId));
        Assert.Equal(OrphanedTranscodeTerminator.StartOf(sameProcess), process.StartedAt);
    }

    [Fact]
    public void Only_stderr_may_be_redirected()
    {
        var startInfo = Shell(windows: "exit 0", unix: "exit 0");
        startInfo.RedirectStandardOutput = true;

        Assert.Throws<ArgumentException>(() => FfmpegProcess.Start(startInfo));
    }

    private static async Task WaitForExitAsync(FfmpegProcess process)
    {
        var deadline = Stopwatch.StartNew();
        while (!process.HasExited || process.ExitCode is null)
        {
            Assert.True(deadline.Elapsed < Patience, "The process never finished: its stderr was not being drained.");
            await Task.Delay(50);
        }
    }

    private static ProcessStartInfo Shell(string windows, string unix) => TestShell.Command(windows, unix);
}
