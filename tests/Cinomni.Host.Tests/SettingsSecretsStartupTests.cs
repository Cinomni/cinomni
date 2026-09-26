using Cinomni.Operations.Settings;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Cinomni.Host.Tests;

/// <summary>
/// The degraded mode an installation without CINOMNI_SECRET_KEY runs in: the real composition still
/// builds, the cipher reports itself unavailable, and exactly one startup warning names the missing
/// variable — never the key itself, because there is not one to leak.
/// </summary>
[Collection(MasterKeyCollection.Serial)]
public sealed class SettingsSecretsStartupTests
{
    private static string ConnectionStringFor(string database) =>
        $"Host=localhost;Port=5442;Database={database};Username=cinomni;Password=cinomni_dev";

    [Fact]
    public void The_real_composition_builds_without_the_master_key_and_the_cipher_reports_unavailable()
    {
        WithMasterKey(null, () =>
        {
            using var provider = ComposedProvider("cinomni_test_settings_secrets_host_absent");
            var cipher = provider.GetRequiredService<SettingsSecretCipher>();
            var reason = cipher.UnavailableReason;

            Assert.False(cipher.IsAvailable);
            Assert.NotNull(reason);
            Assert.Contains(SecretMasterKey.EnvironmentVariableName, reason, StringComparison.Ordinal);
        });
    }

    [Fact]
    public void The_startup_report_warns_exactly_once_and_never_logs_the_key()
    {
        WithMasterKey(null, () =>
        {
            using var provider = ComposedProvider("cinomni_test_settings_secrets_host_report");
            var logger = new RecordingLogger();

            SettingsSecretsStartup.ReportSecretsContract(provider, logger);

            var record = Assert.Single(logger.Records);
            Assert.Equal(LogLevel.Warning, record.Level);
            Assert.Contains(SecretMasterKey.EnvironmentVariableName, record.Message, StringComparison.Ordinal);
        });
    }

    /// <summary>
    /// A canary against a future module that reuses another module's settings key: the real composition
    /// every backend module registers into, checked exactly the way <c>Program</c> checks it before the
    /// Host starts serving traffic.
    /// </summary>
    [Fact]
    public void The_real_composition_registers_no_duplicate_settings_keys()
    {
        using var provider = ComposedProvider("cinomni_test_settings_secrets_host_catalogue");

        SettingsCatalogueStartup.VerifyUniqueKeys(provider);
    }

    [Fact]
    public void An_available_master_key_composes_with_the_cipher_ready_and_produces_no_warning()
    {
        WithMasterKey(Convert.ToBase64String(new byte[32]), () =>
        {
            using var provider = ComposedProvider("cinomni_test_settings_secrets_host_available");
            var logger = new RecordingLogger();

            SettingsSecretsStartup.ReportSecretsContract(provider, logger);

            Assert.Empty(logger.Records);
            Assert.True(provider.GetRequiredService<SettingsSecretCipher>().IsAvailable);
        });
    }

    /// <summary>
    /// The real composition root, exactly what <c>Program</c> calls — not a copy of it — resolved
    /// without touching the database: nothing here calls <c>MigrateAsync</c> or opens a connection.
    /// </summary>
    private static ServiceProvider ComposedProvider(string database)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddCinomniModules(new ConfigurationBuilder().Build(), ConnectionStringFor(database));
        return services.BuildServiceProvider();
    }

    /// <summary>
    /// Sets CINOMNI_SECRET_KEY for the duration of <paramref name="action"/> and restores whatever the
    /// process held before, so this test can never leak state into another one in the same assembly.
    /// </summary>
    private static void WithMasterKey(string? value, Action action)
    {
        var previous = Environment.GetEnvironmentVariable(SecretMasterKey.EnvironmentVariableName);
        Environment.SetEnvironmentVariable(SecretMasterKey.EnvironmentVariableName, value);
        try
        {
            action();
        }
        finally
        {
            Environment.SetEnvironmentVariable(SecretMasterKey.EnvironmentVariableName, previous);
        }
    }

    private sealed record LogRecord(LogLevel Level, string Message);

    private sealed class RecordingLogger : ILogger
    {
        public List<LogRecord> Records { get; } = [];

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter) =>
            Records.Add(new LogRecord(logLevel, formatter(state, exception)));
    }
}
