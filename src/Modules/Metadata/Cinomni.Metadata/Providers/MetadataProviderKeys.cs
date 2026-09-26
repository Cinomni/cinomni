using Cinomni.Operations.Settings;

namespace Cinomni.Metadata.Providers;

/// <summary>
/// The provider credentials as the settings store currently resolves them: an environment variable,
/// else a value an administrator saved from the console (stored encrypted), else the configuration
/// file. Live, so a key entered in the console switches its provider on without a restart.
/// <para>
/// Empty means "nothing resolved here", and an adapter then falls back to the key its own options were
/// composed with — which is how a composition that registers no settings store (a test host) keeps
/// working, and never differs from this value in a running installation.
/// </para>
/// </summary>
public sealed class MetadataProviderKeys
{
    internal const string TmdbApiKeyKey = "metadata.tmdb.apiKey";
    internal const string TvdbApiKeyKey = "metadata.tvdb.apiKey";
    internal const string TvdbPinKey = "metadata.tvdb.pin";

    public string TmdbApiKey { get; init; } = string.Empty;

    public string TvdbApiKey { get; init; } = string.Empty;

    public string TvdbPin { get; init; } = string.Empty;

    /// <summary>The live value when there is one, otherwise the composed one.</summary>
    internal static string Effective(string live, string? composed) =>
        !string.IsNullOrWhiteSpace(live) ? live : composed ?? string.Empty;

    /// <summary>Builds the current value from the settings store's view.</summary>
    internal static MetadataProviderKeys Bind(SettingsView view) => new()
    {
        TmdbApiKey = view.GetString(MetadataProviderKeyDefinitions.TmdbApiKey)?.Trim() ?? string.Empty,
        TvdbApiKey = view.GetString(MetadataProviderKeyDefinitions.TvdbApiKey)?.Trim() ?? string.Empty,
        TvdbPin = view.GetString(MetadataProviderKeyDefinitions.TvdbPin)?.Trim() ?? string.Empty,
    };
}

/// <summary>
/// The settable-key catalogue backed by <see cref="MetadataProviderKeys"/>. Every one is a secret: the
/// store encrypts it at rest and the read endpoint never returns it.
/// </summary>
public static class MetadataProviderKeyDefinitions
{
    public static readonly SettingDefinition TmdbApiKey = Secret(MetadataProviderKeys.TmdbApiKeyKey, "Metadata:Tmdb:ApiKey");

    public static readonly SettingDefinition TvdbApiKey = Secret(MetadataProviderKeys.TvdbApiKeyKey, "Metadata:Tvdb:ApiKey");

    public static readonly SettingDefinition TvdbPin = Secret(MetadataProviderKeys.TvdbPinKey, "Metadata:Tvdb:Pin");

    public static IReadOnlyList<SettingDefinition> All { get; } = [TmdbApiKey, TvdbApiKey, TvdbPin];

    // Not required: an absent key is a supported state (the provider stays off and says so), and the
    // length cap keeps an accidental paste of a whole file out of an HTTP query string.
    private static SettingDefinition Secret(string key, string configurationPath) => new(
        key,
        SettingKind.Secret,
        IsSecret: true,
        configurationPath,
        Validation: new SettingValidation(Required: false, MaxLength: 512));
}
