using Cinomni.Operations.Persistence;
using Cinomni.Operations.Settings;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Cinomni.Operations.Tests;

/// <summary>
/// Integration coverage for the read-only listing behind <c>GET /api/operations/settings</c>: source
/// resolution across the whole precedence chain, and — since none of the eleven real keys this
/// increment wires is secret — a dedicated proof that <see cref="SettingsQuery"/> would still redact one
/// if a fifth property on any of these four options classes ever became a secret-kind setting.
/// </summary>
[Collection(SettingsSecretsCollection.Serial)]
public sealed class SettingsQueryTests : IAsyncLifetime
{
    private const string MasterKeyBase64 =
        "AQIDBAUGBwgJCgsMDQ4PEBESExQVFhcYGRobHB0eHyA="; // 32 arbitrary, fixed bytes — never a real secret

    private static readonly SettingDefinition TextKey = new("test.query.text", SettingKind.Text, false, "Test:Query:Text", "fallback");
    private static readonly SettingDefinition SecretKey = new("test.query.secret", SettingKind.Secret, true, "Test:Query:Secret");

    private ServiceProvider _provider = null!;

    public async Task InitializeAsync()
    {
        // The master key only needs to be present for the moment of composition (SettingsSecretCipher
        // reads it once, in AddOperations), so it is restored before this method returns rather than
        // held for the whole test.
        using var env = new TemporaryEnvironmentVariable(SecretMasterKey.EnvironmentVariableName, MasterKeyBase64);
        _provider = await OperationsTestHost.CreateAsync(
            "cinomni_test_settings_query",
            services =>
            {
                services.AddSettingDefinition(TextKey);
                services.AddSettingDefinition(SecretKey);
            });
    }

    public async Task DisposeAsync() => await _provider.DisposeAsync();

    [Fact]
    public async Task An_unset_key_reports_the_default_source_and_default_value()
    {
        await using var scope = _provider.CreateAsyncScope();
        var query = scope.ServiceProvider.GetRequiredService<SettingsQuery>();

        var settings = await query.ListAsync();
        var text = Assert.Single(settings, s => s.Key == TextKey.Key);

        Assert.Equal("fallback", text.Value);
        Assert.False(text.IsSet);
        Assert.Equal(0, text.Version);
        Assert.Equal(SettingSource.Default, text.Source);
        Assert.Equal("fallback", text.DefaultValue);
    }

    [Fact]
    public async Task A_stored_value_reports_the_database_source_its_version_and_is_set()
    {
        var administration = _provider.GetRequiredService<ISettingsAdministration>();
        var actor = Guid.NewGuid();
        var result = await administration.ApplyAsync([SettingChange.Set(TextKey.Key, "custom", 0)], actor);
        Assert.True(result.IsSuccess);

        await using var scope = _provider.CreateAsyncScope();
        var query = scope.ServiceProvider.GetRequiredService<SettingsQuery>();

        var settings = await query.ListAsync();
        var text = Assert.Single(settings, s => s.Key == TextKey.Key);

        Assert.Equal("custom", text.Value);
        Assert.True(text.IsSet);
        Assert.Equal(1, text.Version);
        Assert.Equal(SettingSource.Database, text.Source);
    }

    /// <summary>
    /// No key in the real four-class catalogue this increment wires is secret, so this proves the
    /// redaction directly against a local secret-kind definition rather than through HTTP.
    /// </summary>
    [Fact]
    public async Task A_secret_key_never_reports_its_value_even_when_set()
    {
        var administration = _provider.GetRequiredService<ISettingsAdministration>();
        var actor = Guid.NewGuid();
        var result = await administration.ApplyAsync([SettingChange.Set(SecretKey.Key, "hunter2", 0)], actor);
        Assert.True(result.IsSuccess);

        await using var scope = _provider.CreateAsyncScope();
        var query = scope.ServiceProvider.GetRequiredService<SettingsQuery>();

        var settings = await query.ListAsync();
        var secret = Assert.Single(settings, s => s.Key == SecretKey.Key);

        Assert.True(secret.IsSecret);
        Assert.Null(secret.Value);
        Assert.True(secret.IsSet);
        Assert.Equal(1, secret.Version);
    }

    [Fact]
    public async Task GetAsync_returns_null_for_a_key_no_definition_names()
    {
        await using var scope = _provider.CreateAsyncScope();
        var query = scope.ServiceProvider.GetRequiredService<SettingsQuery>();

        Assert.Null(await query.GetAsync("not.a.real.key"));
    }

    [Fact]
    public async Task A_key_pinned_by_the_command_line_reports_the_environment_source()
    {
        var pinned = new SettingDefinition("test.query.pinned", SettingKind.Text, false, "Test:Query:Pinned");
        var configuration = new ConfigurationBuilder()
            .AddCommandLine(["--Test:Query:Pinned=from-deployment"])
            .Build();

        await using var provider = await OperationsTestHost.CreateAsync(
            "cinomni_test_settings_query_pinned",
            services => services.AddSettingDefinition(pinned),
            configuration: configuration);

        await using var scope = provider.CreateAsyncScope();
        var query = scope.ServiceProvider.GetRequiredService<SettingsQuery>();

        var settings = await query.ListAsync();
        var setting = Assert.Single(settings, s => s.Key == pinned.Key);

        Assert.Equal(SettingSource.Environment, setting.Source);
        Assert.Equal("from-deployment", setting.Value);
        Assert.False(setting.IsSet);
    }

    private sealed class TemporaryEnvironmentVariable : IDisposable
    {
        private readonly string _name;
        private readonly string? _previous;

        public TemporaryEnvironmentVariable(string name, string value)
        {
            _name = name;
            _previous = Environment.GetEnvironmentVariable(name);
            Environment.SetEnvironmentVariable(name, value);
        }

        public void Dispose() => Environment.SetEnvironmentVariable(_name, _previous);
    }
}
