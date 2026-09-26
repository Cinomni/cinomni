using Cinomni.Import.Application;
using Cinomni.Operations;
using Cinomni.Operations.Persistence;
using Cinomni.Operations.Retention;
using Cinomni.Operations.Settings;
using Cinomni.Subtitles.Application;
using Cinomni.Subtitles.Providers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Cinomni.Host.Tests;

/// <summary>
/// The settings store's shipping criterion for its first increment (store + read path, zero editable
/// keys): an installation that has never written a setting must behave exactly as it does today.
/// <para>
/// Composes the REAL installation — <c>Cinomni.Host.CinomniModules.AddCinomniModules</c>, the same
/// method <c>Program</c> calls — against a real, freshly migrated, and empty
/// <c>operations.setting</c> table. It then registers <see cref="ILiveOptions{TOptions}"/> for a
/// representative set of the options types later increments will move onto the store (this
/// increment registers none in <c>CinomniModules</c> itself), with binders that reproduce each
/// module's own configuration-binding rules through <see cref="SettingsView"/> instead of duplicating
/// them. With the table empty, every read must fall through to the same configuration value the
/// plain singleton the real composition already produced — proving the precedence resolver and the
/// cache reproduce today's behaviour exactly, not just plausibly.
/// </para>
/// <para>
/// <b>Covers:</b> <see cref="SubtitleOptions"/> (list/number/boolean/duration kinds),
/// <see cref="SubtitleProviderOptions.ApiKey"/> (secret kind), <see cref="RetentionOptions"/>'s four
/// cutoff fields (duration/number kinds), and <see cref="ImportOptions"/>'s two size floors (number
/// kind). <b>Does not cover:</b> Metadata (increment 6), the retention window family and schedule
/// cadences (increments 7-8), Downloads seeding defaults (increment 9), or the <c>Enum</c>-kind
/// setting (<c>metadata.tvdb.seasonType</c>) — none of those are exercised by any binder here. The
/// precedence resolver's exact provider-origin detection (env/command-line pin vs. file) is proved
/// separately, with an actual override present, by <c>SettingsReadPathUnitTests</c> and
/// <c>SettingsStoreIntegrationTests</c> in <c>Cinomni.Operations.Tests</c>.
/// </para>
/// </summary>
[Trait("Category", "RequiresDatabase")]
public sealed class SettingsReadPathHostTests : IAsyncLifetime
{
    private static string ConnectionStringFor(string database) =>
        $"Host=localhost;Port=5442;Database={database};Username=cinomni;Password=cinomni_dev";

    private ServiceProvider _provider = null!;

    public async Task InitializeAsync()
    {
        // An installation that has never written a setting: default configuration (no environment,
        // no command line, no JSON file overrides — exactly what a fresh install with only its shipped
        // appsettings.json and no operator changes would present).
        IConfiguration configuration = new ConfigurationBuilder().Build();
        var connectionString = ConnectionStringFor("cinomni_test_settings_readpath");

        var services = new ServiceCollection();
        services.AddLogging();

        // The REAL composition root, not a copy: HostComposition.cs deliberately omits configuration/
        // connectionString (arity 1) for tests that never touch the database, so this calls the
        // internal 3-arg Cinomni.Host.CinomniModules.AddCinomniModules directly (InternalsVisibleTo).
        services.AddCinomniModules(configuration, connectionString);

        RegisterRepresentativeLiveOptions(services, configuration);

        _provider = services.BuildServiceProvider();

        await using var scope = _provider.CreateAsyncScope();
        var operations = scope.ServiceProvider.GetRequiredService<OperationsDbContext>();
        await operations.Database.EnsureDeletedAsync();
        await operations.Database.MigrateAsync();
    }

    public async Task DisposeAsync() => await _provider.DisposeAsync();

    [Fact]
    public async Task The_settings_table_is_empty_after_a_fresh_migration()
    {
        await using var scope = _provider.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<OperationsDbContext>();

        Assert.Equal(0, await dbContext.Settings.CountAsync());
    }

    [Fact]
    public async Task Subtitle_options_resolved_through_the_store_equal_todays_plain_singleton()
    {
        await _provider.LoadSettingsAsync();

        var expected = _provider.GetRequiredService<SubtitleOptions>();
        var actual = _provider.GetRequiredService<ILiveOptions<SubtitleOptions>>().Current;

        Assert.Equal(expected.WantedLanguages, actual.WantedLanguages);
        Assert.Equal(expected.MinScore, actual.MinScore);
        Assert.Equal(expected.HearingImpaired, actual.HearingImpaired);
        Assert.Equal(expected.Forced, actual.Forced);
        Assert.Equal(expected.ProviderCallInterval, actual.ProviderCallInterval);
    }

    [Fact]
    public async Task Subtitle_provider_api_key_resolved_through_the_store_equals_todays_plain_singleton()
    {
        await _provider.LoadSettingsAsync();

        var expected = _provider.GetRequiredService<SubtitleProviderOptions>();
        var actual = _provider.GetRequiredService<ILiveOptions<SubtitleProviderOptions>>().Current;

        Assert.Equal(expected.ApiKey, actual.ApiKey);
    }

    [Fact]
    public async Task Operations_retention_cutoffs_resolved_through_the_store_equal_todays_plain_singleton()
    {
        await _provider.LoadSettingsAsync();

        var expected = _provider.GetRequiredService<RetentionOptions>();
        var actual = _provider.GetRequiredService<ILiveOptions<RetentionOptions>>().Current;

        Assert.Equal(expected.OutboxRetention, actual.OutboxRetention);
        Assert.Equal(expected.CompletedCommandRetention, actual.CompletedCommandRetention);
        Assert.Equal(expected.FailedCommandRetention, actual.FailedCommandRetention);
        Assert.Equal(expected.BatchSize, actual.BatchSize);
    }

    [Fact]
    public async Task Import_size_floors_resolved_through_the_store_equal_todays_plain_singleton()
    {
        await _provider.LoadSettingsAsync();

        var expected = _provider.GetRequiredService<ImportOptions>();
        var actual = _provider.GetRequiredService<ILiveOptions<ImportOptions>>().Current;

        Assert.Equal(expected.MinVideoBytes, actual.MinVideoBytes);
        Assert.Equal(expected.MinEpisodeBytes, actual.MinEpisodeBytes);
    }

    /// <summary>
    /// Binders written the way a later increment's module registration would write them: they read
    /// through <see cref="SettingsView"/> using the same configuration paths
    /// <c>Cinomni.Host.ModuleConfiguration</c> already binds, and fall back to the options type's own
    /// property initializer — never a value hand-copied from the plain singleton, or this test would
    /// prove nothing.
    /// </summary>
    private static void RegisterRepresentativeLiveOptions(IServiceCollection services, IConfiguration configuration)
    {
        var wantedLanguages = new SettingDefinition(
            "subtitles.wantedLanguages", SettingKind.List, IsSecret: false, "Subtitles:WantedLanguages");
        var minScore = new SettingDefinition(
            "subtitles.minScore", SettingKind.Number, IsSecret: false, "Subtitles:MinScore");
        var hearingImpaired = new SettingDefinition(
            "subtitles.hearingImpaired", SettingKind.Boolean, IsSecret: false, "Subtitles:HearingImpaired");
        var forced = new SettingDefinition(
            "subtitles.forced", SettingKind.Boolean, IsSecret: false, "Subtitles:Forced");
        var providerCallInterval = new SettingDefinition(
            "subtitles.providerCallInterval", SettingKind.Duration, IsSecret: false, "Subtitles:ProviderCallInterval");

        services.AddLiveOptions<SubtitleOptions>(view =>
        {
            var fallback = new SubtitleOptions();
            return new SubtitleOptions
            {
                WantedLanguages = view.GetStringArray(wantedLanguages) ?? fallback.WantedLanguages,
                MinScore = view.GetInt(minScore) ?? fallback.MinScore,
                HearingImpaired = view.GetBool(hearingImpaired) ?? fallback.HearingImpaired,
                Forced = view.GetBool(forced) ?? fallback.Forced,
                ProviderCallInterval = view.GetTimeSpan(providerCallInterval) ?? fallback.ProviderCallInterval,
            };
        });

        var subtitleApiKey = new SettingDefinition(
            "subtitles.provider.apiKey", SettingKind.Secret, IsSecret: true, "Subtitles:Provider:ApiKey");

        services.AddLiveOptions<SubtitleProviderOptions>(view =>
        {
            var fallback = new SubtitleProviderOptions();
            return new SubtitleProviderOptions
            {
                ApiKey = view.GetString(subtitleApiKey) ?? fallback.ApiKey,
                // Out of the settings store's scope by design: the base
                // address, user agent and timeout stay bound from configuration only, exactly as
                // Cinomni.Host.ModuleConfiguration.AddConfiguredSubtitleAdapters reads them today.
                BaseAddress = configuration["Subtitles:Provider:BaseAddress"] is { Length: > 0 } address
                    ? address
                    : fallback.BaseAddress,
                UserAgent = configuration["Subtitles:Provider:UserAgent"] is { Length: > 0 } userAgent
                    ? userAgent
                    : fallback.UserAgent,
                Timeout = configuration.GetValue<TimeSpan?>("Subtitles:Provider:Timeout") ?? fallback.Timeout,
            };
        });

        var outboxRetention = new SettingDefinition(
            "retention.operations.outboxRetention", SettingKind.Duration, IsSecret: false, "Retention:OutboxRetention");
        var completedCommandRetention = new SettingDefinition(
            "retention.operations.completedCommandRetention", SettingKind.Duration, IsSecret: false,
            "Retention:CompletedCommandRetention");
        var failedCommandRetention = new SettingDefinition(
            "retention.operations.failedCommandRetention", SettingKind.Duration, IsSecret: false,
            "Retention:FailedCommandRetention");
        var batchSize = new SettingDefinition(
            "retention.operations.batchSize", SettingKind.Number, IsSecret: false, "Retention:BatchSize");

        services.AddLiveOptions<RetentionOptions>(view =>
        {
            var fallback = new RetentionOptions();
            return new RetentionOptions
            {
                OutboxRetention = view.GetTimeSpan(outboxRetention) ?? fallback.OutboxRetention,
                CompletedCommandRetention =
                    view.GetTimeSpan(completedCommandRetention) ?? fallback.CompletedCommandRetention,
                FailedCommandRetention = view.GetTimeSpan(failedCommandRetention) ?? fallback.FailedCommandRetention,
                BatchSize = view.GetInt(batchSize) ?? fallback.BatchSize,
                // Interval is a schedule cadence, never resolved through ILiveOptions (it
                // lives in operations.scheduled_job.interval_seconds instead).
                Interval = fallback.Interval,
            };
        });

        var minVideoBytes = new SettingDefinition(
            "import.minVideoBytes", SettingKind.Number, IsSecret: false, "Import:MinVideoBytes");
        var minEpisodeBytes = new SettingDefinition(
            "import.minEpisodeBytes", SettingKind.Number, IsSecret: false, "Import:MinEpisodeBytes");

        services.AddLiveOptions<ImportOptions>(view =>
        {
            var fallback = new ImportOptions();
            return new ImportOptions
            {
                MinVideoBytes = view.GetLong(minVideoBytes) ?? fallback.MinVideoBytes,
                MinEpisodeBytes = view.GetLong(minEpisodeBytes) ?? fallback.MinEpisodeBytes,
            };
        });
    }
}
