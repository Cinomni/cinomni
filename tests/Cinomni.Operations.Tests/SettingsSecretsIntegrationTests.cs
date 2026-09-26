using Cinomni.Operations.Persistence;
using Cinomni.Operations.Settings;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Cinomni.Operations.Tests;

/// <summary>
/// Integration coverage for the master-key degraded mode against a real PostgreSQL instance: a stored
/// secret row decrypts into the read-path snapshot when the master key is available, and is treated as
/// absent — never thrown on — when it is not. Pure cipher behaviour (tamper, key mismatch, the AAD
/// binding, and the absent-key write refusal) needs no database and is covered by
/// <see cref="SettingsSecretCipherTests"/>.
/// </summary>
[Collection(SettingsSecretsCollection.Serial)]
public sealed class SettingsSecretsIntegrationTests : IAsyncLifetime
{
    private const string MasterKeyBase64 =
        "AQIDBAUGBwgJCgsMDQ4PEBESExQVFhcYGRobHB0eHyA="; // 32 arbitrary, fixed bytes — never a real secret

    private ServiceProvider? _provider;

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        if (_provider is not null)
        {
            await _provider.DisposeAsync();
        }
    }

    [Fact]
    public async Task A_stored_secret_row_decrypts_into_the_snapshot_when_the_master_key_is_available()
    {
        using var env = new TemporaryEnvironmentVariable(SecretMasterKey.EnvironmentVariableName, MasterKeyBase64);
        _provider = await OperationsTestHost.CreateAsync("cinomni_test_settings_secrets_available", _ => { });

        await using (var scope = _provider.CreateAsyncScope())
        {
            var dbContext = scope.ServiceProvider.GetRequiredService<OperationsDbContext>();
            var cipher = scope.ServiceProvider.GetRequiredService<SettingsSecretCipher>();
            Assert.True(cipher.IsAvailable);

            var encrypted = cipher.Encrypt("test.secret", "hunter2").Value;
            dbContext.Settings.Add(SecretRow("test.secret", encrypted));
            await dbContext.SaveChangesAsync();
        }

        await _provider.LoadSettingsAsync();

        await using var verify = _provider.CreateAsyncScope();
        var cache = verify.ServiceProvider.GetRequiredService<SettingsCache>();

        Assert.Equal("hunter2", cache.Current.Values["test.secret"]);
    }

    [Fact]
    public async Task A_stored_secret_row_is_treated_as_absent_rather_than_thrown_on_when_no_master_key_is_available()
    {
        // Written under a real key first — the same shape as an operator losing CINOMNI_SECRET_KEY
        // between the write and a later restart.
        EncryptedSecret encrypted;
        using (new TemporaryEnvironmentVariable(SecretMasterKey.EnvironmentVariableName, MasterKeyBase64))
        {
            var masterKey = SecretMasterKey.TryLoadFromEnvironment(out var reason);
            encrypted = new SettingsSecretCipher(masterKey, reason).Encrypt("test.secret", "hunter2").Value;
        }

        // No CINOMNI_SECRET_KEY set for this composition. Restored in the finally below rather than
        // left cleared, so a class sharing the serialized collection never inherits an absent key.
        var previous = Environment.GetEnvironmentVariable(SecretMasterKey.EnvironmentVariableName);
        Environment.SetEnvironmentVariable(SecretMasterKey.EnvironmentVariableName, null);
        try
        {
            _provider = await OperationsTestHost.CreateAsync("cinomni_test_settings_secrets_unavailable", _ => { });
        }
        finally
        {
            Environment.SetEnvironmentVariable(SecretMasterKey.EnvironmentVariableName, previous);
        }

        await using (var scope = _provider.CreateAsyncScope())
        {
            var dbContext = scope.ServiceProvider.GetRequiredService<OperationsDbContext>();
            dbContext.Settings.Add(SecretRow("test.secret", encrypted));
            await dbContext.SaveChangesAsync();
        }

        await _provider.LoadSettingsAsync();

        await using var verify = _provider.CreateAsyncScope();
        var cache = verify.ServiceProvider.GetRequiredService<SettingsCache>();
        var cipher = verify.ServiceProvider.GetRequiredService<SettingsSecretCipher>();

        Assert.False(cipher.IsAvailable);
        Assert.False(cache.Current.Values.ContainsKey("test.secret"));
        // The loader never threw: the empty-values assertion above is reachable at all only because
        // LoadSettingsAsync completed instead of propagating a decryption failure.
    }

    private static Setting SecretRow(string key, EncryptedSecret encrypted) => new()
    {
        Key = key,
        Value = null,
        SecretCipher = encrypted.Cipher,
        SecretNonce = encrypted.Nonce,
        KeyId = encrypted.KeyId,
        Fingerprint = encrypted.Fingerprint,
        UpdatedAt = DateTimeOffset.UtcNow,
        UpdatedBy = Guid.NewGuid(),
    };

    /// <summary>Sets a process environment variable for the lifetime of the instance, then restores it.</summary>
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
