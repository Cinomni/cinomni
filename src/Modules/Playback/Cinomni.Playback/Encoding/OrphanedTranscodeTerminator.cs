using System.ComponentModel;
using System.Diagnostics;
using Microsoft.Extensions.Logging;

namespace Cinomni.Playback.Encoding;

/// <summary>What became of a process a previous run recorded.</summary>
public enum OrphanOutcome
{
    /// <summary>Nothing runs under the recorded id any more.</summary>
    NotRunning = 1,

    /// <summary>Something runs under the id, but it is not the recorded FFmpeg: left alone.</summary>
    NotOurs = 2,

    /// <summary>It was the recorded FFmpeg, and it has been stopped.</summary>
    Stopped = 3,

    /// <summary>It was the recorded FFmpeg and it could not be stopped: try again later.</summary>
    Survived = 4,
}

/// <summary>
/// Stops an FFmpeg that a previous run of the host left behind. The host owns every process it
/// starts, but that ownership ends with it: in the packaged image the host is the container's first
/// process and takes its children with it, whereas a host run any other way can crash and leave its
/// encoders running with nothing left to stop them.
/// <para>
/// The recorded id and start time come from a database row, and a row is not proof of anything. A
/// process is stopped only when every independent fact agrees: it runs under the recorded id, it
/// started when the row says, it is the configured FFmpeg binary, and — where the operating system
/// will say (Linux) — its own command line names the output directory of that very session, which no
/// other process has any reason to mention. Only that one process is stopped, never a tree: the
/// encoder starts no children. Anything short of all of that is somebody else's, and is left alone.
/// </para>
/// </summary>
public sealed class OrphanedTranscodeTerminator(FfmpegEncoderOptions ffmpeg, ILogger<OrphanedTranscodeTerminator> logger)
{
    /// <summary>
    /// How far the recorded start may be from the running process's. On Windows both are the same fixed
    /// creation time; on Linux both are derived from the boot time, which the runtime reads in whole
    /// seconds and which moves when the wall clock is stepped. A step larger than this leaves the
    /// orphan running — failing safe — and the command-line check is what rules out a reused id.
    /// </summary>
    internal static readonly TimeSpan StartTolerance = TimeSpan.FromSeconds(2);

    private static readonly TimeSpan ExitWait = TimeSpan.FromSeconds(5);

    /// <param name="outputDirectory">The session's output directory, as the recorded job names it.</param>
    public async Task<OrphanOutcome> TryTerminateAsync(int processId, DateTimeOffset startedAt, string outputDirectory)
    {
        if (processId <= 0 || processId == Environment.ProcessId || string.IsNullOrWhiteSpace(outputDirectory))
        {
            return OrphanOutcome.NotOurs;
        }

        Process process;
        try
        {
            process = Process.GetProcessById(processId);
        }
        catch (ArgumentException)
        {
            return OrphanOutcome.NotRunning;
        }

        using (process)
        {
            try
            {
                if (!IsTheRecordedEncoder(process, startedAt, outputDirectory))
                {
                    return OrphanOutcome.NotOurs;
                }

                process.Kill(entireProcessTree: false);
                using var timeout = new CancellationTokenSource(ExitWait);
                await process.WaitForExitAsync(timeout.Token);
                return OrphanOutcome.Stopped;
            }
            catch (InvalidOperationException)
            {
                return OrphanOutcome.NotRunning; // exited while it was being looked at
            }
            catch (Exception failure) when (failure is Win32Exception or NotSupportedException or OperationCanceledException)
            {
                logger.LogWarning(
                    failure, "Could not stop process {ProcessId}, which a previous run left transcoding.", processId);
                return OrphanOutcome.Survived;
            }
        }
    }

    /// <summary>When a process started, in UTC — the form <see cref="FfmpegProcess"/> records it in.</summary>
    internal static DateTimeOffset StartOf(Process process) => new DateTimeOffset(process.StartTime).ToUniversalTime();

    private bool IsTheRecordedEncoder(Process process, DateTimeOffset startedAt, string outputDirectory)
    {
        if ((StartOf(process) - startedAt).Duration() > StartTolerance)
        {
            return false;
        }

        var binary = Path.GetFileNameWithoutExtension(ffmpeg.BinaryPath);
        if (!string.Equals(process.ProcessName, binary, OperatingSystem.IsWindows()
                ? StringComparison.OrdinalIgnoreCase
                : StringComparison.Ordinal))
        {
            return false;
        }

        return !OperatingSystem.IsLinux() || CommandLineNames(process.Id, outputDirectory);
    }

    /// <summary>Whether one of the process's own arguments lies inside <paramref name="outputDirectory"/>.</summary>
    private static bool CommandLineNames(int processId, string outputDirectory)
    {
        string commandLine;
        try
        {
            commandLine = File.ReadAllText($"/proc/{processId}/cmdline");
        }
        catch (Exception failure) when (failure is IOException or UnauthorizedAccessException)
        {
            return false; // cannot tell, so it is not ours to stop
        }

        var directory = Path.TrimEndingDirectorySeparator(outputDirectory) + Path.DirectorySeparatorChar;
        return commandLine.Split('\0').Any(argument => argument.StartsWith(directory, StringComparison.Ordinal));
    }
}
