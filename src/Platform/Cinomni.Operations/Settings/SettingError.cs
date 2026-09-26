namespace Cinomni.Operations.Settings;

/// <summary>
/// One rejected rule from the write-path validation pipeline (<c>validation</c> in the design), attributed
/// to the setting key(s) it judged. A cross-key invariant — e.g. a completed-command retention window
/// that would no longer outlive the outbox window — names every key it depends on, not just the one that
/// happened to be edited, so a caller can highlight all of them rather than guess which one to blame.
/// <para>
/// <see cref="Message"/> must never echo a rejected secret value (SECURITY.md); every producer of a
/// <see cref="SettingError"/> for a secret-kind key is responsible for keeping the value out of the text.
/// </para>
/// </summary>
public sealed record SettingError(IReadOnlyList<string> Keys, string Code, string Message);
