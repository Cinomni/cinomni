using System.Diagnostics;

namespace Cinomni.Playback.Tests;

/// <summary>
/// The operating system's own shell standing in for FFmpeg, for the tests whose subject is the process
/// itself — its pipe, its kill, its identity — rather than what it encodes.
/// </summary>
internal static class TestShell
{
    /// <summary>The binary these processes run, as <c>Playback:Ffmpeg:BinaryPath</c> would name FFmpeg.</summary>
    public static string Binary => OperatingSystem.IsWindows() ? "powershell.exe" : "/bin/sh";

    /// <summary>A process that would run for two minutes if nothing stopped it.</summary>
    public static ProcessStartInfo LongRunning() => Command(windows: "Start-Sleep -Seconds 120", unix: "sleep 120");

    /// <summary>
    /// A long-running process whose own command line carries <paramref name="argument"/>, the way
    /// FFmpeg's names its manifest — and which stays the shell rather than exec'ing into <c>sleep</c>.
    /// <para>
    /// The <c>sleep</c> it forks must not inherit the stderr pipe. Only the shell is ever stopped (the
    /// recorded process, never its tree), and a child still holding the pipe would keep the wrapper's
    /// stderr drain open for the full two minutes, so the stopped shell would never read as exited.
    /// FFmpeg has no such child; this is the stand-in's problem, not the terminator's.
    /// </para>
    /// </summary>
    public static ProcessStartInfo LongRunningNaming(string argument)
    {
        var startInfo = Command(windows: "Start-Sleep -Seconds 120", unix: "sleep 120 2>/dev/null; exit 0");
        if (OperatingSystem.IsWindows())
        {
            startInfo.ArgumentList.Add(argument); // ignored by the script; present on the command line
        }
        else
        {
            startInfo.ArgumentList.Add("sh");     // $0
            startInfo.ArgumentList.Add(argument); // $1
        }

        return startInfo;
    }

    public static ProcessStartInfo Command(string windows, string unix)
    {
        var startInfo = new ProcessStartInfo
        {
            UseShellExecute = false,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };

        if (OperatingSystem.IsWindows())
        {
            startInfo.FileName = Binary;
            startInfo.ArgumentList.Add("-NoProfile");
            startInfo.ArgumentList.Add("-NonInteractive");
            startInfo.ArgumentList.Add("-Command");
            startInfo.ArgumentList.Add(windows);
        }
        else
        {
            startInfo.FileName = Binary;
            startInfo.ArgumentList.Add("-c");
            startInfo.ArgumentList.Add(unix);
        }

        return startInfo;
    }
}
