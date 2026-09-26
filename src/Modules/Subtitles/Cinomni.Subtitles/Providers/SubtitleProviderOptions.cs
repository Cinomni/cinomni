namespace Cinomni.Subtitles.Providers;

/// <summary>
/// Configuration for the OpenSubtitles REST provider. The API key is referenced, never hardcoded
/// or shipped embedded in the application: it is supplied by the operator. The base address is admin-fixed and
/// fetched over an SSRF-hardened client.
/// </summary>
public sealed class SubtitleProviderOptions
{
    public string BaseAddress { get; set; } = "https://api.opensubtitles.com/api/v1/";

    /// <summary>The OpenSubtitles API key (from a secret manager / environment; empty disables the provider).</summary>
    public string ApiKey { get; set; } = string.Empty;

    public string UserAgent { get; set; } = "Cinomni/1.0";

    public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(30);
}
