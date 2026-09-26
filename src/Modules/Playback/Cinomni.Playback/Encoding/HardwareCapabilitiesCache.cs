namespace Cinomni.Playback.Encoding;

/// <summary>
/// The in-process home for the one-time <see cref="IHardwareCapabilityProbe"/> result: a volatile
/// field, published once by the Host after the startup probe runs, read cheaply — no lock, no
/// re-probe — by the planner on every plan. Same shape as
/// <c>Cinomni.Operations.Settings.SettingsCache</c>: registered as a singleton, starts empty so a
/// planner that runs before the startup probe (a test host, for instance) degrades to software
/// rather than failing to resolve.
/// </summary>
public sealed class HardwareCapabilitiesCache
{
    private HardwareCapabilities _capabilities = new([]);

    /// <summary>The most recently published probe result. Never blocks and never re-probes.</summary>
    public HardwareCapabilities Current => Volatile.Read(ref _capabilities);

    public void Publish(HardwareCapabilities capabilities) => Volatile.Write(ref _capabilities, capabilities);
}
