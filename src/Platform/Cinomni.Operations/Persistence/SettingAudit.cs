namespace Cinomni.Operations.Persistence;

/// <summary>The two things that can happen to a stored setting. Append-only: no update, no delete.</summary>
public static class SettingAuditAction
{
    public const string Set = "Set";
    public const string Cleared = "Cleared";
}

/// <summary>
/// One immutable history row for a setting change. For a secret key, <see cref="OldDisplay"/> and
/// <see cref="NewDisplay"/> hold the fingerprint only — never plaintext, never ciphertext
/// (SECURITY.md: a secret value must never reach a log record or a response).
/// </summary>
public sealed class SettingAudit
{
    public Guid Id { get; init; }

    public required string Key { get; init; }

    public DateTimeOffset ChangedAt { get; init; }

    /// <summary>The Identity account that made this change, carried as an opaque id.</summary>
    public Guid ChangedBy { get; init; }

    /// <summary><see cref="SettingAuditAction.Set"/> or <see cref="SettingAuditAction.Cleared"/>.</summary>
    public required string Action { get; init; }

    public string? OldDisplay { get; init; }

    public string? NewDisplay { get; init; }
}
