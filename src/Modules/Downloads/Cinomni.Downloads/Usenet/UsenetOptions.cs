namespace Cinomni.Downloads.Usenet;

/// <summary>
/// The one Usenet server this installation downloads through. Empty <see cref="Host"/> means Usenet
/// is not configured, and a Usenet release then fails instead of being handed to the torrent engine.
/// <para>
/// The password is operator configuration. It is never written to a log, an error, or telemetry.
/// Usenet traffic leaves over this machine's own connection. It is not the torrent tunnel, and a
/// tunnel hold does not stop it.
/// </para>
/// </summary>
public sealed class UsenetOptions
{
    public const string Section = "Downloads:Usenet";

    public string Host { get; set; } = string.Empty;

    /// <summary>Zero means 563 when <see cref="UseTls"/> is on, otherwise 119.</summary>
    public int Port { get; set; }

    public bool UseTls { get; set; } = true;

    public string Username { get; set; } = string.Empty;

    public string Password { get; set; } = string.Empty;

    public bool IsConfigured => !string.IsNullOrWhiteSpace(Host);

    public int EffectivePort => Port > 0 ? Port : UseTls ? 563 : 119;

    public void Validate()
    {
        if (!IsConfigured)
        {
            return;
        }

        if (Host.Trim().Length != Host.Length || Host.Contains(' '))
        {
            throw new InvalidOperationException($"{Section}:Host must be a host name, with no spaces.");
        }

        if (Port is < 0 or > 65535)
        {
            throw new InvalidOperationException($"{Section}:Port must be between 0 and 65535.");
        }
    }
}
