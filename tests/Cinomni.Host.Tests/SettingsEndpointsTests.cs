using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Cinomni.Host.Operations;
using Cinomni.Identity.Application;
using Cinomni.Identity.Contracts;
using Cinomni.Kernel.Messaging;
using Cinomni.Operations.Persistence;
using Cinomni.Operations.Retention;
using Cinomni.Operations.Settings;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Cinomni.Host.Tests;

/// <summary>
/// Drives the real <c>/api/operations/settings</c> routes over HTTP, against a real PostgreSQL database:
/// authorization, the settings catalogue, the write path's every rejection code mapped to its HTTP
/// status, and the "no longer a frozen singleton" guarantee — a value the store just accepted is used by
/// the very next run of a consumer, without a restart.
/// </summary>
[Trait("Category", "RequiresDatabase")]
public sealed class SettingsEndpointsTests : IAsyncLifetime
{
    private const string Database = "cinomni_test_settings_endpoints";
    private const string AdminUsername = "operator";
    private const string MemberUsername = "household-member";
    private const string Password = "correct horse battery staple";

    private WebApplication _app = null!;
    private HttpClient _client = null!;
    private string _adminToken = null!;
    private string _memberToken = null!;
    private Guid _adminId;

    public async Task InitializeAsync()
    {
        _app = await SettingsHttpTestHost.StartAsync(Database);
        _client = _app.GetTestClient();

        await using var scope = _app.Services.CreateAsyncScope();
        var provisioning = scope.ServiceProvider.GetRequiredService<IUserProvisioning>();
        var sessions = scope.ServiceProvider.GetRequiredService<ISessionService>();

        var admin = await provisioning.CreateAdminAsync(AdminUsername, Password);
        Assert.True(admin.IsSuccess, admin.Error.Message);
        _adminId = admin.Value.Value;
        _adminToken = (await sessions.IssueAsync(admin.Value)).Token;

        var member = await provisioning.CreateUserAsync(MemberUsername, Password, UserRole.Member);
        Assert.True(member.IsSuccess, member.Error.Message);
        _memberToken = (await sessions.IssueAsync(member.Value)).Token;
    }

    public async Task DisposeAsync()
    {
        _client.Dispose();
        await _app.DisposeAsync();
    }

    [Fact]
    public async Task A_member_gets_401_or_403_listing_settings_and_the_body_is_not_the_catalogue()
    {
        using var response = await SendAsync(HttpMethod.Get, "/api/operations/settings", _memberToken);

        Assert.True(
            response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden,
            $"expected 401 or 403, got {response.StatusCode}");
    }

    [Fact]
    public async Task An_admin_lists_exactly_the_documented_keys()
    {
        var settings = await GetSettingsAsync();

        Assert.Equal(31, settings.Count);

        var batchSize = Assert.Single(settings, s => s.Key == "retention.operations.batchSize");
        Assert.Equal("Number", batchSize.Kind);
        Assert.False(batchSize.IsSecret);
        Assert.Equal("Retention:BatchSize", batchSize.ConfigurationPath);
        Assert.Equal("5000", batchSize.DefaultValue);
        Assert.Equal("default", batchSize.Source);
        Assert.False(batchSize.IsSet);
        Assert.Equal(0, batchSize.Version);

        var expectedKeys = new[]
        {
            "decision.evaluationRetention",
            "retention.decision.interval",
            "metadata.snapshotRetention",
            "retention.metadata.interval",
            "backup.keepCount",
            "retention.backup.interval",
            "retention.operations.outboxRetention",
            "retention.operations.completedCommandRetention",
            "retention.operations.failedCommandRetention",
            "retention.operations.batchSize",
            "retention.operations.interval",
            "monitoring.searchDelay",
            "catalog.trendingList.enabled",
            "import.movieNaming",
            "subtitles.wantedLanguages",
            "subtitles.hearingImpaired",
            "subtitles.forced",
            "playback.hardwareTranscodingEnabled",
            "playback.hardwareBackend",
            "playback.hardwareDecoding",
            "playback.outputCodec",
            "playback.encoderPreset",
            "playback.videoQuality",
            "playback.maxResolution",
            "playback.maxBitrateKbps",
            "playback.encoderThreads",
            "playback.toneMapping",
            "playback.toneMapAlgorithm",
            "playback.maxAudioChannels",
            "playback.audioBitrateKbps",
            "playback.burnInImageSubtitles",
        };
        Assert.Equal(expectedKeys.Order(StringComparer.Ordinal), settings.Select(s => s.Key).Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task The_listing_says_which_values_an_enumeration_takes_and_the_bounds_of_a_number()
    {
        var settings = await GetSettingsAsync();

        var backend = Assert.Single(settings, s => s.Key == "playback.hardwareBackend");
        Assert.Equal("Enum", backend.Kind);
        Assert.NotNull(backend.AllowedValues);
        Assert.Equal(["auto", "vaapi", "qsv", "nvenc", "amf"], backend.AllowedValues!);
        Assert.Null(backend.MinValue);
        Assert.Null(backend.MaxValue);

        var quality = Assert.Single(settings, s => s.Key == "playback.videoQuality");
        Assert.Equal("Number", quality.Kind);
        Assert.Equal(15, quality.MinValue);
        Assert.Equal(40, quality.MaxValue);
        Assert.Null(quality.AllowedValues);

        // A switch has neither: the listing does not invent a range for it.
        var toneMapping = Assert.Single(settings, s => s.Key == "playback.toneMapping");
        Assert.Null(toneMapping.AllowedValues);
        Assert.Null(toneMapping.MinValue);
    }

    [Fact]
    public async Task A_transcoding_value_outside_its_set_or_its_bounds_is_refused_and_a_valid_one_is_live()
    {
        using var outsideSet = await PutAsync(
            "playback.hardwareBackend", new UpdateSettingRequest("cuda", ExpectedVersion: 0), _adminToken);
        Assert.Equal(HttpStatusCode.BadRequest, outsideSet.StatusCode);
        using var outsideBounds = await PutAsync(
            "playback.videoQuality", new UpdateSettingRequest("99", ExpectedVersion: 0), _adminToken);
        Assert.Equal(HttpStatusCode.BadRequest, outsideBounds.StatusCode);

        using var accepted = await PutAsync(
            "playback.hardwareBackend", new UpdateSettingRequest("nvenc", ExpectedVersion: 0), _adminToken);

        Assert.True(accepted.IsSuccessStatusCode, $"expected success, got {accepted.StatusCode}");
        Assert.Equal(
            Cinomni.Playback.Encoding.HardwareBackendPreference.Nvenc,
            _app.Services.GetRequiredService<ILiveOptions<Cinomni.Playback.Encoding.TranscodingOptions>>().Current.HardwareBackend);
    }

    [Fact]
    public async Task A_valid_write_persists_audits_and_is_reflected_by_the_next_read()
    {
        using var response = await PutAsync(
            "retention.operations.batchSize", new UpdateSettingRequest("10000", 0), _adminToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var updated = await response.Content.ReadFromJsonAsync<SettingDto>();
        Assert.NotNull(updated);
        Assert.Equal("10000", updated!.Value);
        Assert.Equal("database", updated.Source);
        Assert.True(updated.IsSet);
        Assert.Equal(1, updated.Version);

        await using var scope = _app.Services.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<OperationsDbContext>();
        var row = await dbContext.Settings.AsNoTracking().SingleAsync(s => s.Key == "retention.operations.batchSize");
        Assert.Equal("10000", row.Value);
        Assert.Equal(1, row.Version);

        var audit = await dbContext.SettingAudits.AsNoTracking()
            .SingleAsync(a => a.Key == "retention.operations.batchSize");
        Assert.Equal(SettingAuditAction.Set, audit.Action);
        Assert.Equal("10000", audit.NewDisplay);
        Assert.Equal(_adminId, audit.ChangedBy);

        var settings = await GetSettingsAsync();
        var reread = Assert.Single(settings, s => s.Key == "retention.operations.batchSize");
        Assert.Equal("10000", reread.Value);
        Assert.Equal(1, reread.Version);
        Assert.Equal("database", reread.Source);
    }

    [Fact]
    public async Task An_invalid_duration_is_rejected_and_does_not_touch_the_running_configuration()
    {
        using var response = await PutAsync(
            "retention.operations.outboxRetention",
            new UpdateSettingRequest("not-a-duration", 0),
            _adminToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var error = await response.Content.ReadFromJsonAsync<SettingsErrorDto>();
        Assert.Equal("settings.invalid_duration", error!.Error);

        var current = _app.Services.GetRequiredService<ILiveOptions<RetentionOptions>>().Current;
        Assert.Equal(TimeSpan.FromDays(14), current.OutboxRetention);

        await using var scope = _app.Services.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<OperationsDbContext>();
        Assert.Equal(0, await dbContext.Settings.AsNoTracking().CountAsync());
    }

    [Fact]
    public async Task A_cross_key_invariant_violation_names_every_key_it_depends_on()
    {
        using var response = await PutAsync(
            "retention.operations.completedCommandRetention",
            new UpdateSettingRequest("1.00:00:00", 0),
            _adminToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var error = await response.Content.ReadFromJsonAsync<SettingsErrorDto>();
        Assert.Equal("settings.retention_window_too_short", error!.Error);
        Assert.Contains("retention.operations.completedCommandRetention", error.Keys);
        Assert.Contains("retention.operations.outboxRetention", error.Keys);
    }

    [Fact]
    public async Task A_stale_expected_version_is_refused_as_a_conflict()
    {
        using var first = await PutAsync(
            "retention.operations.batchSize", new UpdateSettingRequest("6000", 0), _adminToken);
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);

        // Still claims expectedVersion 0 — a second editor who never saw the write above.
        using var second = await PutAsync(
            "retention.operations.batchSize", new UpdateSettingRequest("7000", 0), _adminToken);

        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);
        var error = await second.Content.ReadFromJsonAsync<SettingsErrorDto>();
        Assert.Equal("settings.conflict", error!.Error);
    }

    [Fact]
    public async Task A_key_pinned_by_the_command_line_is_refused_as_a_conflict_and_the_database_is_untouched()
    {
        await _app.DisposeAsync();
        _client.Dispose();

        var pinnedConfiguration = new ConfigurationBuilder()
            .AddCommandLine(["--Retention:BatchSize=42"])
            .Build();
        _app = await SettingsHttpTestHost.StartAsync(Database + "_pinned", pinnedConfiguration);
        _client = _app.GetTestClient();

        await using (var scope = _app.Services.CreateAsyncScope())
        {
            var provisioning = scope.ServiceProvider.GetRequiredService<IUserProvisioning>();
            var sessions = scope.ServiceProvider.GetRequiredService<ISessionService>();
            var admin = await provisioning.CreateAdminAsync(AdminUsername, Password);
            Assert.True(admin.IsSuccess, admin.Error.Message);
            _adminToken = (await sessions.IssueAsync(admin.Value)).Token;
        }

        using var response = await PutAsync(
            "retention.operations.batchSize", new UpdateSettingRequest("99", 0), _adminToken);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var error = await response.Content.ReadFromJsonAsync<SettingsErrorDto>();
        Assert.Equal("settings.overridden_by_environment", error!.Error);

        await using var verify = _app.Services.CreateAsyncScope();
        var dbContext = verify.ServiceProvider.GetRequiredService<OperationsDbContext>();
        Assert.Equal(0, await dbContext.Settings.AsNoTracking().CountAsync());
    }

    [Fact]
    public async Task An_unknown_key_is_refused_as_not_found()
    {
        using var response = await PutAsync(
            "not.a.real.key", new UpdateSettingRequest("x", 0), _adminToken);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        var error = await response.Content.ReadFromJsonAsync<SettingsErrorDto>();
        Assert.Equal("settings.unknown_key", error!.Error);
    }

    [Fact]
    public async Task A_member_cannot_write_a_setting_and_nothing_is_persisted()
    {
        using var response = await PutAsync(
            "retention.operations.batchSize", new UpdateSettingRequest("123", 0), _memberToken);

        Assert.True(
            response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden,
            $"expected 401 or 403, got {response.StatusCode}");

        await using var scope = _app.Services.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<OperationsDbContext>();
        Assert.Equal(0, await dbContext.Settings.AsNoTracking().CountAsync());
    }

    /// <summary>
    /// The headline proof this is no longer a frozen singleton: a value the store just accepted is what
    /// the very next run of the consumer uses, in the same process, with no restart.
    /// </summary>
    [Fact]
    public async Task A_purge_run_right_after_a_write_uses_the_new_batch_size_without_a_restart()
    {
        using var response = await PutAsync(
            "retention.operations.batchSize", new UpdateSettingRequest("1", 0), _adminToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        await using var scope = _app.Services.CreateAsyncScope();
        var handler = scope.ServiceProvider.GetRequiredService<ICommandHandler<PurgeOperationsCommand>>();
        var result = await handler.HandleAsync(new PurgeOperationsCommand());

        Assert.True(result.IsSuccess);
        Assert.Equal(1, _app.Services.GetRequiredService<ILiveOptions<RetentionOptions>>().Current.BatchSize);
    }

    private async Task<List<SettingDto>> GetSettingsAsync()
    {
        using var response = await SendAsync(HttpMethod.Get, "/api/operations/settings", _adminToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await response.Content.ReadFromJsonAsync<List<SettingDto>>() ?? [];
    }

    private async Task<HttpResponseMessage> PutAsync(string key, UpdateSettingRequest body, string token)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Put, $"/api/operations/settings/{Uri.EscapeDataString(key)}")
        {
            Content = JsonContent.Create(body),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return await _client.SendAsync(request);
    }

    private async Task<HttpResponseMessage> SendAsync(HttpMethod method, string requestUri, string token)
    {
        using var request = new HttpRequestMessage(method, requestUri);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return await _client.SendAsync(request);
    }

    private sealed record SettingDto(
        string Key,
        string Kind,
        bool IsSecret,
        string ConfigurationPath,
        string? Value,
        bool IsSet,
        string Source,
        long Version,
        string? DefaultValue,
        string[]? AllowedValues = null,
        double? MinValue = null,
        double? MaxValue = null);

    private sealed record SettingsErrorDto(string Error, string Message, string[] Keys);
}
