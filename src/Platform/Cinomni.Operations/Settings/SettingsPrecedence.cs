using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Configuration.CommandLine;
using Microsoft.Extensions.Configuration.EnvironmentVariables;

namespace Cinomni.Operations.Settings;

/// <summary>
/// Detects whether a configuration path is pinned by the deployment (an environment variable or a
/// command-line argument), by walking the real provider chain rather than guessing from value shape.
/// <para>
/// Only environment variables and the command line pin. <c>appsettings.json</c> already ships defaults
/// for most settings; if a JSON file counted as a pin, the database would never be consulted for any
/// key a packaged installation ships a default for.
/// </para>
/// </summary>
public static class SettingsPrecedence
{
    /// <summary>
    /// True when the provider that currently answers <paramref name="configurationPath"/> is the
    /// environment-variables or command-line provider, with a value that is not blank.
    /// <para>
    /// Walks <see cref="IConfigurationRoot.Providers"/> in reverse — the same last-registered-wins
    /// order <see cref="IConfiguration"/> itself resolves a key in — so this reports exactly which
    /// provider answered <c>configuration[path]</c>, not an inferred guess.
    /// </para>
    /// </summary>
    public static bool IsPinned(IConfiguration configuration, string configurationPath)
    {
        if (configuration is not IConfigurationRoot root)
        {
            // A configuration this is not the root of (e.g. a bound section) cannot be walked; treat
            // as unpinned so resolution falls through to the database/default tiers instead of
            // throwing on a shape the caller has no control over.
            return false;
        }

        foreach (var provider in root.Providers.Reverse())
        {
            if (provider.TryGet(configurationPath, out var value))
            {
                // Blank is not a value anybody gave. A compose file forwards `${VAR:-}`, so every unset
                // variable arrives as an empty one; pinning on it would hide a stored value behind nothing.
                return provider is EnvironmentVariablesConfigurationProvider or CommandLineConfigurationProvider
                    && !string.IsNullOrWhiteSpace(value);
            }
        }

        return false;
    }
}
