namespace Cinomni.Metadata.Providers;

/// <summary>
/// Shared configuration for an external metadata provider fetched over an SSRF-hardened client. API keys
/// are referenced from a secret manager / environment, never hardcoded;
/// the base address is admin-fixed. Each concrete provider derives its own options so a distinct instance
/// is injected per adapter.
/// </summary>
public abstract class MetadataProviderOptions
{
    /// <summary>The provider's API base address (admin-fixed, fetched over the SSRF-hardened client).</summary>
    public abstract string BaseAddress { get; set; }

    public string UserAgent { get; set; } = "Cinomni/1.0";

    /// <summary>Metadata language (BCP-47), e.g. <c>en-US</c>. Also seeds the artwork language preference.</summary>
    public string Language { get; set; } = "en-US";

    public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(30);
}

/// <summary>Configuration for the TMDB REST provider (movies and series). Empty <see cref="ApiKey"/> disables it.</summary>
public sealed class TmdbProviderOptions : MetadataProviderOptions
{
    public override string BaseAddress { get; set; } = "https://api.themoviedb.org/3/";

    /// <summary>The TMDB API key (from a secret manager / environment; empty disables the provider).</summary>
    public string ApiKey { get; set; } = string.Empty;

    /// <summary>Base for building absolute artwork URLs from a TMDB relative image path.</summary>
    public string ImageBaseAddress { get; set; } = "https://image.tmdb.org/t/p/original";

    /// <summary>
    /// How many season-detail requests one series refresh may issue. TMDB does not embed episodes, so the
    /// structure costs one round trip per season; the cap bounds the fan-out against a rate-limited
    /// provider when a record claims an implausible number of seasons.
    /// </summary>
    public int MaxSeasonRequests { get; set; } = 50;
}

/// <summary>
/// Configuration for TheTVDB v4 REST provider (movies and series). Auth is a bearer JWT obtained by
/// exchanging <see cref="ApiKey"/> (and optional subscriber <see cref="Pin"/>) at <c>/login</c>. Empty
/// <see cref="ApiKey"/> disables the provider. Artwork URLs come back absolute.
/// </summary>
public sealed class TvdbProviderOptions : MetadataProviderOptions
{
    public override string BaseAddress { get; set; } = "https://api4.thetvdb.com/v4/";

    public string ApiKey { get; set; } = string.Empty;

    /// <summary>Optional subscriber PIN for user-supported keys (blank for project/company keys).</summary>
    public string? Pin { get; set; }

    /// <summary>
    /// Which season ordering to request — <c>official</c>, <c>dvd</c> or <c>absolute</c> (see
    /// <see cref="SeasonOrders"/>). This is a <b>correctness</b> setting, not a preference: it decides
    /// which SxxEyy numbers land in the catalog and therefore which release matches which episode. The
    /// ordering used is recorded on every snapshot so a later refresh under a different one is
    /// recognisable. An unknown value falls back to <see cref="SeasonOrders.Official"/>.
    /// </summary>
    public string SeasonType { get; set; } = SeasonOrders.Official;

    /// <summary>
    /// How many pages of the paged episode endpoint one refresh may walk. TheTVDB serves 500 episodes per
    /// page, so the default covers any real series while bounding a pathological <c>links.next</c> chain.
    /// </summary>
    public int MaxEpisodePages { get; set; } = 20;
}

/// <summary>
/// Configuration for the TVMaze REST provider (series only — no key required). It declares no movie
/// support, so the kind-based router never selects it for a movie work.
/// </summary>
public sealed class TvMazeProviderOptions : MetadataProviderOptions
{
    public override string BaseAddress { get; set; } = "https://api.tvmaze.com/";
}
