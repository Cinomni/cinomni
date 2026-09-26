using System.ComponentModel;
using System.Diagnostics;

namespace Cinomni.Playback.Encoding;

/// <summary>
/// One FFmpeg child process and everything needed to end it cleanly. Its stderr is drained from the
/// moment it starts until it closes — the pipe has a fixed buffer, and an encoder that fills it blocks
/// on its next write, which on a long transcode means the stream simply stops — and only a bounded
/// tail of it is kept (<see cref="BoundedLineTail"/>).
/// <para>
/// Disposing kills the whole process tree and waits a bounded time for it to go. It is idempotent and
/// never throws, because it runs on every way a session can end, including a host shutting down.
/// </para>
/// </summary>
internal sealed class FfmpegProcess : IRunningTranscode
{
    /// <summary>How long disposal waits for a killed FFmpeg to exit and its pipe to close.</summary>
    private static readonly TimeSpan ExitWait = TimeSpan.FromSeconds(5);

    private readonly Process _process;
    private readonly BoundedLineTail _stderr = new();
    private readonly Task _drain;
    private int _disposed;
    private int? _exitCode;

    private FfmpegProcess(Process process)
    {
        _process = process;
        _drain = DrainAsync(process.StandardError, _stderr);
        ProcessId = process.Id;
        try
        {
            StartedAt = OrphanedTranscodeTerminator.StartOf(process);
        }
        catch (Exception)
        {
            // Already gone, or the platform will not say: without a start time the id is not an identity,
            // so nothing is recorded that a later run could mistake for somebody else's process.
            ProcessId = null;
        }
    }

    public int? ProcessId { get; }

    public DateTimeOffset? StartedAt { get; }

    /// <summary>
    /// True once the process has exited <b>and</b> its stderr has been read to the end, so that
    /// <see cref="Diagnostics"/> already holds the last thing it said by the time anyone asks why.
    /// </summary>
    public bool HasExited
    {
        get
        {
            if (Volatile.Read(ref _disposed) == 1)
            {
                return true;
            }

            try
            {
                return _drain.IsCompleted && _process.HasExited;
            }
            catch (InvalidOperationException)
            {
                return true; // disposed between the check above and this read
            }
        }
    }

    public int? ExitCode
    {
        get
        {
            if (_exitCode is { } known)
            {
                return known;
            }

            try
            {
                return _drain.IsCompleted && _process.HasExited ? _process.ExitCode : null;
            }
            catch (InvalidOperationException)
            {
                return _exitCode;
            }
        }
    }

    public string Diagnostics => _stderr.ToString();

    /// <summary>
    /// Starts the process described by <paramref name="startInfo"/>, which must redirect stderr and
    /// only stderr. Throws what <see cref="Process.Start()"/> throws — a missing binary is a
    /// <see cref="Win32Exception"/> — so the caller reports a transcode that could not start.
    /// </summary>
    public static FfmpegProcess Start(ProcessStartInfo startInfo)
    {
        if (!startInfo.RedirectStandardError || startInfo.RedirectStandardOutput || startInfo.RedirectStandardInput)
        {
            throw new ArgumentException("FFmpeg must run with stderr redirected and nothing else.", nameof(startInfo));
        }

        var process = new Process { StartInfo = startInfo };
        try
        {
            if (!process.Start())
            {
                throw new InvalidOperationException("Failed to start the FFmpeg process.");
            }
        }
        catch
        {
            process.Dispose();
            throw;
        }

        try
        {
            return new FfmpegProcess(process);
        }
        catch
        {
            // Started, but not yet handed to anything that could stop it: stop it here or never.
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch (Exception failure) when (failure is InvalidOperationException or Win32Exception or AggregateException)
            {
                // Already gone.
            }

            process.Dispose();
            throw;
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1)
        {
            return;
        }

        try
        {
            if (!_process.HasExited)
            {
                // The whole tree: an encoder may have helpers of its own, and an orphaned one keeps the
                // device, the CPU and the output directory busy exactly like the parent did.
                _process.Kill(entireProcessTree: true);
            }
        }
        catch (Exception failure) when (failure is InvalidOperationException or Win32Exception or AggregateException)
        {
            // Exited between the check and the kill, is already being torn down by the OS, or part of
            // its tree could not be signalled (AggregateException). The wait below is what decides.
        }

        using var timeout = new CancellationTokenSource(ExitWait);
        try
        {
            await _process.WaitForExitAsync(timeout.Token);
            await _drain.WaitAsync(timeout.Token);
            _exitCode = _process.ExitCode;
        }
        catch (OperationCanceledException)
        {
            // A process that outlives the kill signal is left to the OS; nothing here can do more.
        }
        finally
        {
            _process.Dispose();
        }
    }

    private static async Task DrainAsync(StreamReader stderr, BoundedLineTail tail)
    {
        // Fixed-size reads, not ReadLineAsync: a stream with no line break would otherwise buffer
        // without bound before the tail ever got to cut it.
        var buffer = new char[1024];
        try
        {
            int read;
            while ((read = await stderr.ReadAsync(buffer.AsMemory())) > 0)
            {
                tail.Append(buffer.AsSpan(0, read));
            }
        }
        catch (Exception failure) when (failure is IOException or ObjectDisposedException)
        {
            // The pipe closed under us: the process is gone, which is what ends the drain anyway.
        }
    }
}
