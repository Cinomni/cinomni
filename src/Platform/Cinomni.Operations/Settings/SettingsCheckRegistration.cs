namespace Cinomni.Operations.Settings;

/// <summary>
/// Gate (b)+(c) of the write-path validation pipeline, bundled: a module's binder rebuilds its options
/// type from the candidate merged view (the trial bind, gate c — the safety net for a rule that lives in
/// no <c>Check()</c> at all, such as <c>new Uri(options.BaseAddress)</c> in <c>SubtitlesModule</c> and
/// <c>MetadataModule</c>), and on success the module's own pure <c>Check(candidate)</c> runs (gate b, the
/// cross-key invariants). Both gates share one binder call because they answer the same question — "is
/// this candidate installation usable" — from two different failure modes: one an exception, the other a
/// list of <see cref="SettingError"/>.
/// </summary>
/// <param name="Keys">
/// Every setting key this group's binder can read. The write path only re-evaluates a group whose keys
/// intersect the batch being applied.
/// </param>
/// <param name="Evaluate">Runs the bind-then-check pair against one candidate <see cref="SettingsView"/>.</param>
public sealed record SettingsCheckRegistration(
    IReadOnlyList<string> Keys,
    Func<SettingsView, IEnumerable<SettingError>> Evaluate);
