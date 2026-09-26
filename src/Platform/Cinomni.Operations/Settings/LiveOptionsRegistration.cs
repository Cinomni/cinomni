using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Cinomni.Operations.Settings;

/// <summary>Registers hot-reloadable options types on the settings store's read path.</summary>
public static class LiveOptionsRegistration
{
    /// <summary>
    /// Registers <typeparamref name="TOptions"/> as an <see cref="ILiveOptions{TOptions}"/> singleton.
    /// The binder rebuilds a full <typeparamref name="TOptions"/> from a <see cref="SettingsView"/>
    /// whenever the settings generation changes. No <see cref="SettingDefinition"/> needs to exist for
    /// this to work — a binder that never looks anything up simply reproduces whatever the options
    /// type's own property initializers and the underlying <see cref="IConfiguration"/> already give it,
    /// unaffected by an empty (or absent) settings store.
    /// </summary>
    public static IServiceCollection AddLiveOptions<TOptions>(
        this IServiceCollection services, Func<SettingsView, TOptions> binder)
        where TOptions : class
    {
        services.AddSingleton<ILiveOptions<TOptions>>(sp => new LiveOptions<TOptions>(
            sp.GetRequiredService<SettingsCache>(),
            sp.GetRequiredService<IConfiguration>(),
            binder));

        return services;
    }
}
