using Cinomni.Operations.Settings;

namespace Cinomni.Playback.Encoding;

/// <summary>The settable-key catalogue backed by <see cref="HardwareTranscodingOptions"/>: its one property.</summary>
public static class HardwareTranscodingSettingDefinitions
{
    public static readonly SettingDefinition Enabled = new(
        HardwareTranscodingOptions.EnabledKey,
        SettingKind.Boolean,
        IsSecret: false,
        "Playback:HardwareTranscodingEnabled",
        "true",
        new SettingValidation(Required: true));
}
