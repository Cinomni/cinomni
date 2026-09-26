using Cinomni.Kernel.Messaging;
using Cinomni.Library.Contracts;
using Cinomni.Operations.Messaging;
using Cinomni.Subtitles.Application;
using Cinomni.Subtitles.Messaging;
using Cinomni.Subtitles.Providers;

namespace Cinomni.Subtitles.EventHandlers;

/// <summary>
/// Reacts to Library's <see cref="MediaAssetRegistered"/> by searching for the asset's missing
/// subtitle languages (subtitle search is triggered by this event). Enqueues a command
/// (the provider fetch and file write are out-of-process side effects that must be retryable);
/// idempotent by <c>search-subtitles:{assetId}</c>. The event carries only a summary, so the search
/// service queries Library by interface for the file path and streams.
/// <para>
/// The command is <b>scheduled</b>, not merely queued: a season pack registers N assets at once and the
/// platform runs one sequential command worker, so pacing the provider inside the handler would hold
/// that worker — and every other module's commands — for (N-1) intervals. Booking the asset's slots on
/// <see cref="SubtitleProviderThrottle"/> and deferring the command to the first of them keeps the rate
/// limit while the worker stays free. With no usable provider nothing is booked at all.
/// </para>
/// </summary>
public sealed class MediaAssetRegisteredHandler(
    ICommandQueue commandQueue,
    SubtitleProviderThrottle throttle,
    IEnumerable<ISubtitleProvider> providers,
    SubtitleOptions options) : IEventHandler<MediaAssetRegistered>
{
    public async Task HandleAsync(MediaAssetRegistered domainEvent, CancellationToken cancellationToken = default)
    {
        var command = new SearchSubtitlesCommand(domainEvent.AssetId);
        var key = $"search-subtitles:{domainEvent.AssetId}";

        // Book the worst case the command can spend — one call per wanted language per usable provider.
        // Over-booking only spreads the schedule further; under-booking would let the next asset's slot
        // arrive while this one is still calling out, and the gate would then hold the worker.
        var runAfter = throttle.Reserve(providers.Count(p => p.IsConfigured) * options.WantedLanguages.Count);
        if (runAfter is null)
        {
            await commandQueue.EnqueueAsync(command, key, cancellationToken);
            return;
        }

        await commandQueue.EnqueueAtAsync(command, key, runAfter.Value, cancellationToken);
    }
}
