namespace Cinomni.Downloads.Engine;

/// <summary>
/// The one qBittorrent Web API session this process holds, shared by every engine instance. The engine
/// is a typed HTTP client, so a new one is made per scope and per readiness probe; kept on the
/// instance, the session meant a fresh sign-in each time — one every few seconds from the anonymous
/// readiness endpoint alone.
/// <para>
/// A refused sign-in is remembered for <see cref="RefusalHoldOff"/>. qBittorrent bans the address
/// that fails to sign in a few times in a row, and in a Compose network that address is often the
/// gateway the operator's own browser comes through: retrying a wrong password on every probe kept the
/// ban in place for as long as the misconfiguration lasted, and for an hour after it was fixed.
/// </para>
/// </summary>
public sealed class QbittorrentSession(TimeProvider? clock = null)
{
    public static readonly TimeSpan RefusalHoldOff = TimeSpan.FromMinutes(5);

    private readonly TimeProvider _clock = clock ?? TimeProvider.System;
    private DateTimeOffset _refusedUntil = DateTimeOffset.MinValue;

    internal SemaphoreSlim Gate { get; } = new(1, 1);

    internal string? Sid { get; set; }

    /// <summary>Whether a recent refusal still holds off the next sign-in.</summary>
    internal bool IsHeldOff => _clock.GetUtcNow() < _refusedUntil;

    internal void RecordRefusal() => _refusedUntil = _clock.GetUtcNow() + RefusalHoldOff;
}
