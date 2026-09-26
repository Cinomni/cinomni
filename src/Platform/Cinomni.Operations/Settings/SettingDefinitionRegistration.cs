using Microsoft.Extensions.DependencyInjection;

namespace Cinomni.Operations.Settings;

/// <summary>Registers the code-defined catalogue of settable keys and their write-path validation gates.</summary>
public static class SettingDefinitionRegistration
{
    /// <summary>
    /// Registers one <see cref="SettingDefinition"/> with no cross-key rule of its own — gate (a), the
    /// per-key kind/range/regex/enum check, is the only gate a standalone key needs.
    /// </summary>
    public static IServiceCollection AddSettingDefinition(this IServiceCollection services, SettingDefinition definition)
    {
        ValidateSecretConsistency(definition);
        services.AddSingleton(definition);
        return services;
    }

    /// <summary>
    /// Registers every definition in <paramref name="definitions"/> and, alongside them, the group's
    /// combined trial-bind-then-check evaluator (gates b+c — see <see cref="SettingsCheckRegistration"/>).
    /// </summary>
    /// <param name="binder">
    /// Rebuilds <typeparamref name="TOptions"/> from a candidate <see cref="SettingsView"/>. Any exception
    /// it throws is caught and reported as a <see cref="SettingError"/> naming every key in the group,
    /// never the exception's own message when the group contains a secret key (SECURITY.md).
    /// </param>
    /// <param name="check">The module's own pure rule, exactly what <c>Validate()</c> now wraps.</param>
    public static IServiceCollection AddSettingsCheck<TOptions>(
        this IServiceCollection services,
        IReadOnlyList<SettingDefinition> definitions,
        Func<SettingsView, TOptions> binder,
        Func<TOptions, IEnumerable<SettingError>> check)
        where TOptions : class
    {
        ArgumentNullException.ThrowIfNull(definitions);
        ArgumentNullException.ThrowIfNull(binder);
        ArgumentNullException.ThrowIfNull(check);

        foreach (var definition in definitions)
        {
            ValidateSecretConsistency(definition);
            services.AddSingleton(definition);
        }

        var keys = definitions.Select(d => d.Key).ToArray();
        var isSecretGroup = definitions.Any(d => d.IsSecret);

        services.AddSingleton(new SettingsCheckRegistration(keys, view => Evaluate(view, binder, check, keys, isSecretGroup)));
        return services;
    }

    private static IEnumerable<SettingError> Evaluate<TOptions>(
        SettingsView view,
        Func<SettingsView, TOptions> binder,
        Func<TOptions, IEnumerable<SettingError>> check,
        IReadOnlyList<string> keys,
        bool isSecretGroup)
        where TOptions : class
    {
        TOptions candidate;
        try
        {
            candidate = binder(view);
        }
        catch (Exception ex)
        {
            var detail = isSecretGroup ? "the value could not be used" : ex.Message;
            return
            [
                new SettingError(
                    keys,
                    "settings.invalid_configuration",
                    $"This combination of settings would produce an unusable configuration: {detail}"),
            ];
        }

        var errors = check(candidate).ToList();

        // The module's own Check() message is free to interpolate a rejected value (see e.g.
        // RetentionOptions.Check), which is safe only because no group registered here today contains a
        // secret key. A future secret-bearing group must not have that value echoed into the HTTP error
        // body, so every message is withheld exactly as the binder-exception branch above already does.
        return isSecretGroup ? errors.Select(Redact).ToList() : errors;
    }

    private static SettingError Redact(SettingError error) =>
        error with
        {
            Message = "This combination of settings would produce an unusable configuration: "
                + "the value could not be used.",
        };

    /// <summary>
    /// A <see cref="SettingKind.Secret"/> definition must always be authored with <c>IsSecret: true</c>:
    /// <see cref="SettingsQuery"/> and the encryption path in <see cref="SettingsAdministration"/> both
    /// key off <see cref="SettingDefinition.IsSecret"/> alone, so a mismatched pair here would store the
    /// value in plaintext and serve it back over the read endpoint.
    /// </summary>
    private static void ValidateSecretConsistency(SettingDefinition definition)
    {
        if (definition.Kind == SettingKind.Secret && !definition.IsSecret)
        {
            throw new ArgumentException(
                $"'{definition.Key}' is declared SettingKind.Secret but IsSecret is false. "
                + "A secret-kind definition must always be authored with IsSecret: true.",
                nameof(definition));
        }
    }
}
