using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace Cinomni.Metadata.Providers;

/// <summary>
/// Says, once per process and per provider, that a metadata provider is switched off for want of the
/// credential it needs — and names the configuration key that would switch it on.
/// <para>
/// A provider with no key neither succeeds nor fails: it returns nothing, writes nothing and raises
/// nothing, which from the outside is indistinguishable from a provider that simply had no match. The
/// log line is the only evidence that it never asked, so it is not optional.
/// </para>
/// <para>
/// Once, rather than per call, is why this is a singleton rather than a latch on the adapter: the
/// adapters are transient typed clients, so a per-instance flag would fire once per scope and a
/// library-wide refresh sweep would write one line per work per provider.
/// </para>
/// <para>
/// Only the <i>name</i> of the key is ever logged. A credential value never reaches a log record
/// (SECURITY.md, secrets and credentials).
/// </para>
/// </summary>
public sealed class DisabledProviderNotice(ILogger<DisabledProviderNotice> logger)
{
    private readonly ConcurrentDictionary<string, byte> _announced = new(StringComparer.Ordinal);

    /// <param name="provider">The provider as an operator names it, for example <c>TMDB</c>.</param>
    /// <param name="configurationKey">The key that configures it, for example <c>Metadata:Tmdb:ApiKey</c>.</param>
    /// <param name="consequence">What the installation does instead, in one sentence.</param>
    public void AnnounceOnce(string provider, string configurationKey, string consequence)
    {
        if (!_announced.TryAdd(configurationKey, 0))
        {
            return;
        }

        logger.LogWarning(
            "{Provider} is disabled: no API key is configured. Enter one under Settings > Metadata, or set "
            + "{ConfigurationKey}. {Consequence}",
            provider,
            configurationKey,
            consequence);
    }
}
