using Microsoft.Extensions.DependencyInjection;

namespace Cinomni.Operations.Settings;

/// <summary>
/// Fails fast at composition time when two modules register a <see cref="SettingDefinition"/> for the
/// same key. <see cref="SettingsAdministration.ApplyAsync"/> builds a dictionary keyed by
/// <see cref="SettingDefinition.Key"/> on every write, so an undetected collision would throw an
/// unhandled <see cref="ArgumentException"/> on the first PUT that touches it rather than stop the Host
/// at startup with the offending key named. Mirrors <see cref="SettingsSecretsStartup.ReportSecretsContract"/>'s
/// shape: called once, after every module has registered its catalogue.
/// </summary>
public static class SettingsCatalogueStartup
{
    public static void VerifyUniqueKeys(IServiceProvider services)
    {
        var duplicates = services.GetServices<SettingDefinition>()
            .GroupBy(definition => definition.Key, StringComparer.Ordinal)
            .Where(group => group.Count() > 1)
            .Select(group => group.Key)
            .ToList();

        if (duplicates.Count > 0)
        {
            throw new InvalidOperationException(
                "More than one module registered a settings definition for the same key: "
                + $"{string.Join(", ", duplicates)}. Every settable key must be owned by exactly one "
                + "SettingDefinition.");
        }
    }
}
