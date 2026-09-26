using System.Collections.Immutable;
using Cinomni.Operations.Settings;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Cinomni.Operations.Tests;

/// <summary>
/// Gates (b)+(c) of the write-path validation pipeline, in isolation: no database, no
/// <see cref="ISettingsAdministration"/>. Proves the generic trial-bind-then-check machinery
/// <see cref="SettingDefinitionRegistration.AddSettingsCheck{TOptions}"/> wires — the case
/// <c>SubtitlesModule.cs:93</c> and <c>MetadataModule.cs:153</c> both leave unguarded today
/// (<c>new Uri(options.BaseAddress)</c>, thrown from nowhere any <c>Validate()</c> reaches) — using a
/// representative binder rather than either module, since increment 4 wires no real key yet.
/// </summary>
public sealed class SettingsCheckRegistrationTests
{
    private static readonly SettingDefinition BaseAddress =
        new("test.provider.baseAddress", SettingKind.Text, false, "Test:BaseAddress");

    private sealed record ProviderOptions(Uri BaseAddress);

    [Fact]
    public void A_malformed_base_address_is_caught_by_the_trial_bind_rather_than_throwing_out_of_evaluate()
    {
        var registration = RegisterAndResolve<ProviderOptions>(
            [BaseAddress],
            view => new ProviderOptions(new Uri(view.GetString(BaseAddress)!)),
            _ => []);

        var errors = registration.Evaluate(ViewWith((BaseAddress.Key, "not a uri"))).ToList();

        var error = Assert.Single(errors);
        Assert.Equal("settings.invalid_configuration", error.Code);
        Assert.Equal([BaseAddress.Key], error.Keys);
    }

    [Fact]
    public void A_trial_bind_failure_for_a_secret_group_never_echoes_the_exception_message()
    {
        var secret = new SettingDefinition("test.provider.apiKey", SettingKind.Secret, true, "Test:ApiKey");
        const string RawSecret = "hunter2-should-never-appear";

        var registration = RegisterAndResolve<ProviderOptions>(
            [secret],
            _ => throw new InvalidOperationException($"could not use key '{RawSecret}'"),
            _ => []);

        var error = Assert.Single(registration.Evaluate(ViewWith((secret.Key, RawSecret))));

        Assert.DoesNotContain(RawSecret, error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_successful_bind_still_runs_the_modules_own_check()
    {
        var registration = RegisterAndResolve<ProviderOptions>(
            [BaseAddress],
            view => new ProviderOptions(new Uri(view.GetString(BaseAddress)!)),
            candidate => candidate.BaseAddress.Scheme == "http"
                ? [new SettingError([BaseAddress.Key], "test.insecure_scheme", "must use https.")]
                : []);

        var errors = registration.Evaluate(ViewWith((BaseAddress.Key, "http://example.test"))).ToList();

        var error = Assert.Single(errors);
        Assert.Equal("test.insecure_scheme", error.Code);
    }

    [Fact]
    public void A_successful_bind_that_passes_the_check_yields_no_errors()
    {
        var registration = RegisterAndResolve<ProviderOptions>(
            [BaseAddress],
            view => new ProviderOptions(new Uri(view.GetString(BaseAddress)!)),
            _ => []);

        Assert.Empty(registration.Evaluate(ViewWith((BaseAddress.Key, "https://example.test"))));
    }

    /// <summary>
    /// Gate (b) — the module's own <c>Check(candidate)</c>, not just the trial-bind exception branch above
    /// — must also withhold a rejected secret value for a group containing one, exactly the gap the review
    /// found open: the check's own <see cref="SettingError.Message"/> can legally interpolate the value it
    /// rejects (see <c>RetentionOptions.Check</c>), and nothing forced that message through the same
    /// redaction the exception branch already applies.
    /// </summary>
    [Fact]
    public void A_check_failure_for_a_secret_group_never_echoes_the_rejected_value_either()
    {
        var secret = new SettingDefinition("test.provider.apiKey", SettingKind.Secret, true, "Test:ApiKey");
        const string RawSecret = "hunter2-should-never-appear";

        var registration = RegisterAndResolve<ProviderOptions>(
            [secret],
            view => new ProviderOptions(new Uri("https://example.test")),
            _ => [new SettingError([secret.Key], "test.rejected", $"the value '{RawSecret}' is not usable.")]);

        var error = Assert.Single(registration.Evaluate(ViewWith((secret.Key, RawSecret))));

        Assert.DoesNotContain(RawSecret, error.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// A <see cref="SettingKind.Secret"/> definition authored with <c>IsSecret: false</c> would be stored
    /// in plaintext and served back over the read endpoint — <see cref="SettingsQuery"/> and the encryption
    /// path both key off <see cref="SettingDefinition.IsSecret"/> alone. Rejected at registration, for both
    /// entry points into the catalogue.
    /// </summary>
    [Fact]
    public void A_secret_kind_definition_without_IsSecret_is_rejected_by_AddSettingDefinition()
    {
        var services = new ServiceCollection();
        var mismatched = new SettingDefinition("test.mismatched", SettingKind.Secret, false, "Test:Mismatched");

        Assert.Throws<ArgumentException>(() => services.AddSettingDefinition(mismatched));
    }

    [Fact]
    public void A_secret_kind_definition_without_IsSecret_is_rejected_by_AddSettingsCheck()
    {
        var services = new ServiceCollection();
        var mismatched = new SettingDefinition("test.mismatched", SettingKind.Secret, false, "Test:Mismatched");

        Assert.Throws<ArgumentException>(() => services.AddSettingsCheck<ProviderOptions>(
            [mismatched], _ => new ProviderOptions(new Uri("https://example.test")), _ => []));
    }

    private static SettingsCheckRegistration RegisterAndResolve<TOptions>(
        IReadOnlyList<SettingDefinition> definitions,
        Func<SettingsView, TOptions> binder,
        Func<TOptions, IEnumerable<SettingError>> check)
        where TOptions : class
    {
        var services = new ServiceCollection();
        services.AddSettingsCheck(definitions, binder, check);
        return services.BuildServiceProvider().GetRequiredService<SettingsCheckRegistration>();
    }

    private static SettingsView ViewWith(params (string Key, string Value)[] values)
    {
        var builder = ImmutableDictionary.CreateBuilder<string, string>(StringComparer.Ordinal);
        foreach (var (key, value) in values)
        {
            builder[key] = value;
        }

        var snapshot = new SettingsSnapshot(1, builder.ToImmutable());
        return new SettingsView(snapshot, new ConfigurationBuilder().Build());
    }
}
