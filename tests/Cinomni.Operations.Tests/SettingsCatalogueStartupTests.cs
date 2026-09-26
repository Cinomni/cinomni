using Cinomni.Operations.Settings;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Cinomni.Operations.Tests;

/// <summary>
/// The composition-time guard against two modules registering a <see cref="SettingDefinition"/> for the
/// same key: <see cref="SettingsAdministration.ApplyAsync"/> builds a dictionary keyed by
/// <see cref="SettingDefinition.Key"/> on every write, so an undetected collision would throw an
/// unhandled exception on the first write that happens to touch it rather than stop the Host at startup.
/// </summary>
public sealed class SettingsCatalogueStartupTests
{
    [Fact]
    public void A_catalogue_with_no_duplicate_keys_passes()
    {
        var services = new ServiceCollection();
        services.AddSettingDefinition(new SettingDefinition("test.one", SettingKind.Text, false, "Test:One"));
        services.AddSettingDefinition(new SettingDefinition("test.two", SettingKind.Text, false, "Test:Two"));

        SettingsCatalogueStartup.VerifyUniqueKeys(services.BuildServiceProvider());
    }

    [Fact]
    public void Two_modules_registering_the_same_key_stop_composition_naming_the_key()
    {
        var services = new ServiceCollection();
        services.AddSettingDefinition(new SettingDefinition("test.duplicate", SettingKind.Text, false, "Test:First"));
        services.AddSettingDefinition(new SettingDefinition("test.duplicate", SettingKind.Number, false, "Test:Second"));

        var exception = Assert.Throws<InvalidOperationException>(
            () => SettingsCatalogueStartup.VerifyUniqueKeys(services.BuildServiceProvider()));

        Assert.Contains("test.duplicate", exception.Message, StringComparison.Ordinal);
    }
}
