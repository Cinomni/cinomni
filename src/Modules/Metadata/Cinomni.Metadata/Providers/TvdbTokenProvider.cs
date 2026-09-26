using System.Net.Http.Json;
using System.Text.Json;
using Cinomni.Operations.Settings;
using Microsoft.Extensions.Logging;

namespace Cinomni.Metadata.Providers;

/// <summary>
/// Singleton that owns TheTVDB v4 bearer token: it exchanges the API key (+ optional PIN) at
/// <c>/login</c> and caches the JWT until it nears expiry, serialising concurrent logins behind a gate.
/// A singleton because the typed provider adapter is transient, so a per-instance cache would never hit.
/// Login goes over the SSRF-hardened named client (<see cref="AuthClientName"/>). Failures degrade to a
/// null token — the adapter then returns no data and the refresh backs off.
/// </summary>
public sealed class TvdbTokenProvider(
    IHttpClientFactory httpClientFactory,
    TvdbProviderOptions options,
    DisabledProviderNotice notice,
    ILogger<TvdbTokenProvider> logger,
    // The key and PIN as the settings store resolves them now; optional so a composition with no store
    // reads the composed ones.
    ILiveOptions<MetadataProviderKeys>? keys = null)
{
    /// <summary>The key that switches this provider on; named in the log line, never its value.</summary>
    private const string ApiKeyConfigurationKey = "Metadata:Tvdb:ApiKey";

    /// <summary>Name of the SSRF-hardened HttpClient used only for the login exchange.</summary>
    public const string AuthClientName = "tvdb-auth";

    // TVDB tokens are valid ~1 month; refresh well before expiry to avoid a 401 mid-flight.
    private static readonly TimeSpan TokenLifetime = TimeSpan.FromDays(25);

    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <summary>
    /// The token and its expiry as one immutable value, swapped whole. Two fields read without a lock
    /// could be seen half-updated — a token checked as present and then read back as the null an
    /// invalidation had just written.
    /// </summary>
    private CachedToken? _cached;

    /// <summary>A token belongs to the credential that obtained it: a replaced key or PIN signs in again.</summary>
    private sealed record CachedToken(string Value, DateTimeOffset ExpiresAt, Credential Credential);

    private sealed record Credential(string ApiKey, string? Pin)
    {
        // A record prints its members; this one must never put a key into a log line or exception.
        public override string ToString() => nameof(Credential);
    }

    /// <summary>Whether a key is resolved right now; read per use because the console can change it.</summary>
    public bool HasKey => !string.IsNullOrEmpty(CurrentCredential().ApiKey);

    private Credential CurrentCredential()
    {
        var live = keys?.Current;
        var apiKey = MetadataProviderKeys.Effective(live?.TvdbApiKey ?? string.Empty, options.ApiKey);
        var pin = MetadataProviderKeys.Effective(live?.TvdbPin ?? string.Empty, options.Pin);
        return new Credential(apiKey, pin.Length == 0 ? null : pin);
    }

    public async Task<string?> GetTokenAsync(CancellationToken cancellationToken)
    {
        var credential = CurrentCredential();
        if (string.IsNullOrEmpty(credential.ApiKey))
        {
            AnnounceDisabledOnce();
            return null;
        }

        if (Current(credential) is { } cached)
        {
            return cached;
        }

        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (Current(credential) is { } renewed)
            {
                return renewed;
            }

            var token = await LoginAsync(credential, cancellationToken);
            if (token is not null)
            {
                Volatile.Write(ref _cached, new CachedToken(token, DateTimeOffset.UtcNow + TokenLifetime, credential));
            }

            return token;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Forgets <paramref name="rejected"/> after the provider refused it (a 401), so the next call
    /// signs in again. The token is cached for 25 days on a lifetime nobody promised: a key rotated or
    /// a token revoked early otherwise failed every call until that clock ran out. Only the token
    /// that was refused is forgotten — one a concurrent caller has already replaced stays.
    /// </summary>
    public void Invalidate(string rejected)
    {
        var cached = Volatile.Read(ref _cached);
        if (cached is not null && string.Equals(cached.Value, rejected, StringComparison.Ordinal))
        {
            // Only if it is still the one that was refused: a renewal that landed meanwhile stays.
            Interlocked.CompareExchange(ref _cached, null, cached);
        }
    }

    private string? Current(Credential credential) =>
        Volatile.Read(ref _cached) is { } cached
        && cached.Credential == credential
        && DateTimeOffset.UtcNow < cached.ExpiresAt
            ? cached.Value
            : null;

    /// <summary>
    /// Says once that the provider is switched off for want of a key. Without it a series search with no
    /// TVDB key degrades to the key-less providers with nothing anywhere explaining the missing results
    /// — which reads exactly like a broken pipeline. Public because the adapter is now skipped rather
    /// than called when it has no key, and something still has to say so. The once-per-process latch
    /// lives in <see cref="DisabledProviderNotice"/>, shared with TMDB, so both providers say this the
    /// same way and neither can quietly stop saying it.
    /// </summary>
    public void AnnounceDisabledOnce() => notice.AnnounceOnce(
        "TheTVDB",
        ApiKeyConfigurationKey,
        "Series searches and refreshes will return results from the remaining providers only.");

    private async Task<string?> LoginAsync(Credential credential, CancellationToken cancellationToken)
    {
        try
        {
            var client = httpClientFactory.CreateClient(AuthClientName);
            var payload = new { apikey = credential.ApiKey, pin = credential.Pin };
            using var response = await client.PostAsJsonAsync("login", payload, cancellationToken);
            response.EnsureSuccessStatusCode();
            var raw = await response.Content.ReadAsStringAsync(cancellationToken);
            using var document = JsonDocument.Parse(raw);
            if (document.RootElement.TryGetProperty("data", out var data)
                && data.ValueKind == JsonValueKind.Object
                && data.TryGetProperty("token", out var token)
                && token.ValueKind == JsonValueKind.String)
            {
                return token.GetString();
            }

            logger.LogWarning("TVDB login returned no token.");
            return null;
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or TaskCanceledException)
        {
            logger.LogWarning(ex, "TVDB login failed.");
            return null;
        }
    }
}
