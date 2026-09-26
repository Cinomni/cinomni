using System.Collections.Concurrent;
using System.Text;
using Cinomni.Subtitles.Contracts;
using Cinomni.Subtitles.Providers;

namespace Cinomni.Subtitles.Tests;

/// <summary>
/// In-memory <see cref="ISubtitleProvider"/> that scripts the candidates and content, so the module's
/// orchestration (search → evaluate → download → land) is tested deterministically without a live
/// provider. The real OpenSubtitles REST client is a deployment concern, exercised outside the unit
/// tests.
/// </summary>
internal sealed class FakeSubtitleProvider : ISubtitleProvider
{
    public string Name => "fake";

    /// <summary>Settable so a test can reproduce the default install: the adapter is registered, but unusable.</summary>
    public bool IsConfigured { get; set; } = true;

    public List<ProviderCandidate> Candidates { get; set; } =
        [new ProviderCandidate("Movie.2024.1080p", 90, HearingImpaired: false, SubtitleFormat.Srt, "file-1")];

    public byte[] Content { get; set; } =
        Encoding.UTF8.GetBytes("1\n00:00:01,000 --> 00:00:02,000\nHello\n");

    /// <summary>Every query the module sent, in a concurrent collection so a parallel burst is recordable.</summary>
    public ConcurrentBag<SubtitleProviderQuery> Searches { get; } = [];

    public Task<IReadOnlyList<ProviderCandidate>> SearchAsync(SubtitleProviderQuery query, CancellationToken cancellationToken = default)
    {
        Searches.Add(query);
        return Task.FromResult<IReadOnlyList<ProviderCandidate>>(Candidates);
    }

    public Exception? DownloadFailure { get; set; }

    public Task<ProviderDownload> DownloadAsync(string downloadRef, CancellationToken cancellationToken = default)
    {
        if (DownloadFailure is not null)
        {
            throw DownloadFailure;
        }

        return Task.FromResult(new ProviderDownload(Content, SubtitleFormat.Srt));
    }
}
