using Cinomni.Operations.Settings;

namespace Cinomni.Metadata.Application;

/// <summary>
/// Which region's age classification this installation uses.
/// <para>
/// One region, not all of them. Classifications are regional — the same film is "PG-13" in the United
/// States, "12" in Spain and "15" in Sweden — and a household watches in one place. Keeping every
/// region a provider publishes would be storing answers to a question nobody asked, and would push a
/// region picker into every surface that shows a rating.
/// </para>
/// <para>
/// <b>There is no default, and that is the point.</b> Unset means Metadata reports no classification
/// at all rather than guessing that an installation is American because the provider lists that one
/// first. It is the same discipline as the rest of this system — the tunnel guard is inert until a
/// device is named, upgrades arrive switched off, hardware acceleration is an overlay nobody applies
/// by accident — and it matters more here than usual: a wrong region does not fail, it silently
/// classifies a library under rules that do not apply to it, and a parental control built on that
/// would be enforcing the wrong thing while looking like it worked.
/// </para>
/// </summary>
public sealed class ContentRatingOptions
{
    internal const string RegionKey = "metadata.contentRatingRegion";

    /// <summary>
    /// An ISO 3166-1 alpha-2 country code, or empty when the installation has not said. Empty means
    /// every classification reads as absent, which is what an installation that never configures this
    /// has always had.
    /// </summary>
    public string Region { get; init; } = string.Empty;

    /// <summary>Whether this installation has named a region to classify by.</summary>
    public bool IsConfigured => !string.IsNullOrWhiteSpace(Region);
}

/// <summary>The settable-key catalogue backed by <see cref="ContentRatingOptions"/>: its one property.</summary>
public static class ContentRatingSettingDefinitions
{
    public static readonly SettingDefinition Region = new(
        ContentRatingOptions.RegionKey,
        SettingKind.Text,
        IsSecret: false,
        "Metadata:ContentRatingRegion",
        string.Empty,
        // Not required: empty is the shipped state and the only honest one before an administrator
        // says where they are. Two letters when it is set, so a typo is refused rather than stored
        // and then silently matching nothing.
        new SettingValidation(Required: false, MaxLength: 2, Pattern: "^([A-Za-z]{2})?$"));
}
