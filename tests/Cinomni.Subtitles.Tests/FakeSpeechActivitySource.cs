using System.Collections.Concurrent;
using Cinomni.Subtitles.Sync;

namespace Cinomni.Subtitles.Tests;

/// <summary>
/// In-memory <see cref="ISpeechActivitySource"/>: answers each stretch with speech from
/// <see cref="Speech"/>, a failure from <see cref="Failure"/>, or throws <see cref="Exception"/>, so a
/// sync pass is driven end to end without FFmpeg or a real film. Records every request.
/// </summary>
internal sealed class FakeSpeechActivitySource : ISpeechActivitySource
{
    public Func<TimeSpan, TimeSpan, SpeechWindow>? Speech { get; set; }

    public string? Failure { get; set; }

    public Exception? Exception { get; set; }

    public ConcurrentBag<(string MediaPath, int AudioStream, TimeSpan Start)> Requests { get; } = [];

    public Task<SpeechActivity> DetectAsync(
        string mediaPath,
        int audioStreamIndex,
        TimeSpan start,
        TimeSpan length,
        CancellationToken cancellationToken = default)
    {
        Requests.Add((mediaPath, audioStreamIndex, start));
        if (Exception is not null)
        {
            throw Exception;
        }

        if (Failure is not null || Speech is null)
        {
            return Task.FromResult(SpeechActivity.Failed(Failure ?? "no speech scripted"));
        }

        return Task.FromResult(SpeechActivity.Found(Speech(start, length).Speech));
    }
}
