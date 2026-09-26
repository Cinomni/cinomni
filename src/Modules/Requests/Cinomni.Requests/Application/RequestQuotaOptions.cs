using Cinomni.Operations.Settings;

namespace Cinomni.Requests.Application;

/// <summary>
/// How many requests one account may have <em>open</em> at once, for accounts that name no limit of
/// their own.
/// <para>
/// Open means pending, or approved and not yet playable. A rejected request and a fulfilled one both
/// release the slot, which is what makes this self-cleaning: the cap is on how much of the
/// administrator's decision queue one person may occupy, not on how much they may ask for over a
/// lifetime. That is the complaint a household actually has, and it needs no window and no clock.
/// </para>
/// <para>
/// Volume is already bounded by something better than a number: every request passes a human
/// approval. This exists for the case where one person fills that queue and nobody else can get a
/// decision.
/// </para>
/// <para>
/// <b>There is deliberately no download quota per account, and there cannot usefully be one.</b> An
/// acquisition carries no account: once a request is approved the title enters the catalog and the
/// spine takes over from <c>WorkAdded</c>, driven by monitoring policy rather than by a person. A
/// download is started by a sweep for what is missing, by an upgrade past the cutoff, or by an
/// operator's interactive search — and a season pack can satisfy episodes three different people
/// asked for. Attributing that to an account would mean threading a requester through five modules
/// to answer a question that has no correct answer. Bounding disk or bandwidth is a real need and a
/// different feature: it belongs to the installation, not to an account.
/// </para>
/// </summary>
public sealed class RequestQuotaOptions
{
    internal const string DefaultOpenRequestLimitKey = "requests.defaultOpenRequestLimit";

    /// <summary>
    /// Zero, which means no limit — the same behaviour every installation has today.
    /// <para>
    /// Shipped off on purpose. An installation that upgrades into this must not discover that its
    /// household has been silently capped, the same reasoning that ships upgrades switched off on a
    /// library that predates them. An administrator turns it on.
    /// </para>
    /// </summary>
    public int DefaultOpenRequestLimit { get; init; }
}

/// <summary>The settable-key catalogue backed by <see cref="RequestQuotaOptions"/>: its one property.</summary>
public static class RequestQuotaSettingDefinitions
{
    public static readonly SettingDefinition DefaultOpenRequestLimit = new(
        RequestQuotaOptions.DefaultOpenRequestLimitKey,
        SettingKind.Number,
        IsSecret: false,
        "Requests:DefaultOpenRequestLimit",
        "0",
        // Zero is meaningful here — it is how the limit is turned off — so the shared Count preset,
        // whose floor is one, would make the off position unreachable through the very surface an
        // operator uses to change it.
        new SettingValidation(Required: true, MinValue: 0, MaxValue: 1_000, MaxLength: 6, Pattern: "^[0-9]+$"));
}
