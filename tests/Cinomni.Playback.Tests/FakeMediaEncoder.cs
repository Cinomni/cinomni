using Cinomni.Playback.Contracts;
using Cinomni.Playback.Encoding;

namespace Cinomni.Playback.Tests;

/// <summary>
/// In-memory <see cref="IMediaEncoder"/> that records transcode requests and hands back a manifest
/// path without touching FFmpeg — so the module's orchestration is tested deterministically. Real
/// HLS is a deployment concern (FFmpeg absent in dev), exercised outside the unit tests. Set
/// <see cref="Fail"/> to model a transcode that cannot start at all, <see cref="Cancel"/> to model the
/// request going away while FFmpeg starts, or <see cref="FallenBackTo"/> to model the bounded
/// hardware-to-software fallback <c>FfmpegMediaEncoder</c> performs itself.
/// <para>
/// Every start hands back a <see cref="FakeRunningTranscode"/> (in <see cref="Processes"/>), so a test
/// can see whether anything ever stopped it. With <see cref="WritesOutput"/> the start also leaves a
/// manifest and a segment in the output directory, as FFmpeg would, so reclaiming it is observable.
/// </para>
/// </summary>
internal sealed class FakeMediaEncoder : IMediaEncoder
{
    public bool Fail { get; set; }

    /// <summary>Thrown instead of starting, when set — to model a specific failure, such as FFmpeg's own.</summary>
    public Exception? Failure { get; set; }

    /// <summary>Runs while FFmpeg is "starting", after the output exists and before the process is handed back.</summary>
    public Func<Task>? OnStart { get; set; }

    public bool Cancel { get; set; }

    public bool WritesOutput { get; set; }

    public EncoderBackend? FallenBackTo { get; set; }

    public List<TranscodeRequest> Requests { get; } = [];

    public List<FakeRunningTranscode> Processes { get; } = [];

    public async Task<TranscodeOutput> StartHlsAsync(TranscodeRequest request, CancellationToken cancellationToken = default)
    {
        Requests.Add(request);
        if (WritesOutput)
        {
            Directory.CreateDirectory(request.OutputDirectory);
            File.WriteAllText(Path.Combine(request.OutputDirectory, "manifest.m3u8"), "#EXTM3U\n");
            File.WriteAllBytes(Path.Combine(request.OutputDirectory, "seg_000.ts"), [0x47, 0x40, 0x00, 0x10]);
        }

        if (OnStart is { } onStart)
        {
            await onStart();
        }

        if (Failure is { } failure)
        {
            throw failure;
        }

        if (Fail)
        {
            throw new InvalidOperationException("encoder failed to start");
        }

        if (Cancel)
        {
            throw new OperationCanceledException("the request went away while FFmpeg was starting");
        }

        var process = new FakeRunningTranscode();
        Processes.Add(process);
        var backendUsed = FallenBackTo ?? request.Backend;
        return new TranscodeOutput(Path.Combine(request.OutputDirectory, "manifest.m3u8"), backendUsed, process);
    }
}

/// <summary>A transcode process a test can end on its own terms, and ask whether anything stopped it.</summary>
internal sealed class FakeRunningTranscode : IRunningTranscode
{
    public bool Stopped { get; private set; }

    public bool HasExited => Stopped || ExitCode is not null;

    /// <summary>A fake has no operating-system identity: nothing a later run could find and stop.</summary>
    public int? ProcessId => null;

    public DateTimeOffset? StartedAt => null;

    public int? ExitCode { get; private set; }

    public string Diagnostics { get; private set; } = string.Empty;

    /// <summary>FFmpeg ending by itself: 0 once it has written the whole file, anything else on an error.</summary>
    public void Exit(int code, string diagnostics = "")
    {
        ExitCode = code;
        Diagnostics = diagnostics;
    }

    public ValueTask DisposeAsync()
    {
        Stopped = true;
        return ValueTask.CompletedTask;
    }
}
