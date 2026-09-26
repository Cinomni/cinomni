using Cinomni.Operations.Persistence;
using Cinomni.Operations.Retention;
using Cinomni.Operations.Settings;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Cinomni.Operations.Tests;

/// <summary>
/// Integration coverage for the write path against a real PostgreSQL
/// instance: resolution, the environment-pin refusal, the candidate-merged-view cross-key gate (using the
/// real <see cref="RetentionOptions.Check"/>, not a stand-in), per-key optimistic concurrency, and the
/// post-commit publish. Most tests register their own throwaway <see cref="SettingDefinition"/> so this
/// suite proves the write-path mechanics in isolation; the two cross-key tests instead exercise
/// <c>AddOperations</c>'s own production <see cref="RetentionSettingDefinitions"/> catalogue, which is
/// already registered by every <see cref="OperationsTestHost"/> composition.
/// </summary>
public sealed class SettingsAdministrationTests : IAsyncLifetime
{
    private static readonly SettingDefinition TestValue = new("test.value", SettingKind.Text, false, "Test:Value");

    private ServiceProvider _provider = null!;

    public async Task InitializeAsync()
    {
        _provider = await OperationsTestHost.CreateAsync(
            "cinomni_test_settings_administration",
            services => services.AddSettingDefinition(TestValue));
    }

    public async Task DisposeAsync() => await _provider.DisposeAsync();

    [Fact]
    public async Task An_unknown_key_is_refused_and_writes_nothing()
    {
        var administration = _provider.GetRequiredService<ISettingsAdministration>();

        var result = await administration.ApplyAsync(
            [SettingChange.Set("no.such.key", "x", 0)], Guid.NewGuid());

        Assert.True(result.IsFailure);
        Assert.Equal("settings.unknown_key", Assert.Single(result.Errors).Code);
        await AssertNoRowsAsync();
    }

    [Fact]
    public async Task A_key_pinned_by_the_command_line_is_refused_with_its_own_code_and_writes_nothing()
    {
        var pinnedDefinition = new SettingDefinition("test.pinned", SettingKind.Text, false, "Test:Pinned");
        var configuration = new ConfigurationBuilder().AddCommandLine(["--Test:Pinned=set-by-deployment"]).Build();

        await using var pinnedProvider = await OperationsTestHost.CreateAsync(
            "cinomni_test_settings_administration_pinned",
            services => services.AddSettingDefinition(pinnedDefinition),
            configuration: configuration);

        var administration = pinnedProvider.GetRequiredService<ISettingsAdministration>();

        var result = await administration.ApplyAsync(
            [SettingChange.Set(pinnedDefinition.Key, "from-the-panel", 0)], Guid.NewGuid());

        Assert.True(result.IsFailure);
        Assert.Equal("settings.overridden_by_environment", Assert.Single(result.Errors).Code);

        await using var scope = pinnedProvider.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<OperationsDbContext>();
        Assert.Equal(0, await dbContext.Settings.AsNoTracking().CountAsync());
    }

    [Fact]
    public async Task A_successful_set_persists_the_row_publishes_a_new_snapshot_and_writes_an_audit_row()
    {
        var administration = _provider.GetRequiredService<ISettingsAdministration>();
        var cache = _provider.GetRequiredService<SettingsCache>();
        var actor = Guid.NewGuid();
        var generationBefore = cache.Current.Generation;

        var result = await administration.ApplyAsync([SettingChange.Set(TestValue.Key, "hello", 0)], actor);

        Assert.True(result.IsSuccess);
        Assert.True(cache.Current.Generation > generationBefore);
        Assert.Equal("hello", cache.Current.Values[TestValue.Key]);

        await using var scope = _provider.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<OperationsDbContext>();
        var row = await dbContext.Settings.AsNoTracking().SingleAsync(s => s.Key == TestValue.Key);
        Assert.Equal("hello", row.Value);
        Assert.Equal(1, row.Version);
        Assert.Equal(actor, row.UpdatedBy);

        var audit = await dbContext.SettingAudits.AsNoTracking().SingleAsync(a => a.Key == TestValue.Key);
        Assert.Equal(SettingAuditAction.Set, audit.Action);
        Assert.Null(audit.OldDisplay);
        Assert.Equal("hello", audit.NewDisplay);
        Assert.Equal(actor, audit.ChangedBy);
    }

    [Fact]
    public async Task A_stale_version_yields_conflict_writes_nothing_and_leaves_the_snapshot_untouched()
    {
        var administration = _provider.GetRequiredService<ISettingsAdministration>();
        var cache = _provider.GetRequiredService<SettingsCache>();
        var actor = Guid.NewGuid();

        var first = await administration.ApplyAsync([SettingChange.Set(TestValue.Key, "v1", 0)], actor);
        Assert.True(first.IsSuccess);
        var generationAfterFirstWrite = cache.Current.Generation;

        // Still claims expectedVersion 0 — as if this caller never saw the write above.
        var second = await administration.ApplyAsync([SettingChange.Set(TestValue.Key, "v2", 0)], actor);

        Assert.True(second.IsFailure);
        Assert.Equal("settings.conflict", Assert.Single(second.Errors).Code);
        Assert.Equal(generationAfterFirstWrite, cache.Current.Generation);
        Assert.Equal("v1", cache.Current.Values[TestValue.Key]);

        await using var scope = _provider.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<OperationsDbContext>();
        var row = await dbContext.Settings.AsNoTracking().SingleAsync(s => s.Key == TestValue.Key);
        Assert.Equal("v1", row.Value);
        Assert.Equal(1, row.Version);
    }

    [Fact]
    public async Task Clearing_an_existing_key_removes_the_row_and_writes_a_cleared_audit_row()
    {
        var administration = _provider.GetRequiredService<ISettingsAdministration>();
        var cache = _provider.GetRequiredService<SettingsCache>();
        var actor = Guid.NewGuid();

        var setResult = await administration.ApplyAsync([SettingChange.Set(TestValue.Key, "hello", 0)], actor);
        Assert.True(setResult.IsSuccess);

        var clearResult = await administration.ApplyAsync([SettingChange.Clear(TestValue.Key, 1)], actor);

        Assert.True(clearResult.IsSuccess);
        Assert.False(cache.Current.Values.ContainsKey(TestValue.Key));

        await using var scope = _provider.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<OperationsDbContext>();
        Assert.Equal(0, await dbContext.Settings.AsNoTracking().CountAsync(s => s.Key == TestValue.Key));

        var audits = await dbContext.SettingAudits.AsNoTracking()
            .Where(a => a.Key == TestValue.Key)
            .OrderBy(a => a.ChangedAt)
            .ToListAsync();
        Assert.Equal(2, audits.Count);
        Assert.Equal(SettingAuditAction.Cleared, audits[1].Action);
        Assert.Equal("hello", audits[1].OldDisplay);
        Assert.Null(audits[1].NewDisplay);
    }

    [Fact]
    public async Task Clearing_a_key_that_was_never_set_is_a_no_op()
    {
        var administration = _provider.GetRequiredService<ISettingsAdministration>();

        var result = await administration.ApplyAsync([SettingChange.Clear(TestValue.Key, 0)], Guid.NewGuid());

        Assert.True(result.IsSuccess);
        await AssertNoRowsAsync();
    }

    /// <summary>
    /// The headline case: a batch that only edits <c>completedCommandRetention</c> is still rejected
    /// against what the installation would become, because the untouched <c>outboxRetention</c> — still
    /// at its default, never stored — is merged into the candidate <see cref="RetentionOptions"/> the real
    /// <see cref="RetentionOptions.Check"/> judges. Per-key gate (a) alone could never catch this: neither
    /// value is individually invalid.
    /// <para>
    /// Uses the production catalogue <see cref="RetentionSettingDefinitions"/> that <c>AddOperations</c>
    /// now registers for real (increment 8) rather than a local stand-in: a second, duplicate-keyed
    /// registration here would collide with it in <see cref="ISettingsAdministration.ApplyAsync"/>'s
    /// catalogue lookup.
    /// </para>
    /// </summary>
    [Fact]
    public async Task The_cross_key_invariant_is_rejected_against_the_candidate_merged_view_and_writes_nothing()
    {
        await using var provider = await OperationsTestHost.CreateAsync(
            "cinomni_test_settings_administration_cross_key", services => { });

        var administration = provider.GetRequiredService<ISettingsAdministration>();

        // Only completedCommandRetention is in this batch; outboxRetention stays at its 14-day default.
        var result = await administration.ApplyAsync(
            [SettingChange.Set(RetentionSettingDefinitions.CompletedCommandRetention.Key, "1.00:00:00", 0)],
            Guid.NewGuid());

        Assert.True(result.IsFailure);
        var error = Assert.Single(result.Errors);
        Assert.Equal("settings.retention_window_too_short", error.Code);
        Assert.Contains(RetentionSettingDefinitions.CompletedCommandRetention.Key, error.Keys);
        Assert.Contains(RetentionSettingDefinitions.OutboxRetention.Key, error.Keys);

        await using var scope = provider.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<OperationsDbContext>();
        Assert.Equal(0, await dbContext.Settings.AsNoTracking().CountAsync());
    }

    /// <summary>
    /// The concrete scenario the settings-store review found unguarded: a duration close to
    /// <see cref="TimeSpan.MaxValue"/> parses cleanly and satisfies every cross-key rule
    /// <see cref="RetentionOptions.Check"/> knows about, but throws once the purge handler subtracts it
    /// from <see cref="DateTimeOffset.UtcNow"/>. It must never reach persistence.
    /// </summary>
    [Fact]
    public async Task A_duration_near_TimeSpan_MaxValue_is_rejected_at_gate_a_and_writes_nothing()
    {
        await using var provider = await OperationsTestHost.CreateAsync(
            "cinomni_test_settings_administration_duration_ceiling", services => { });

        var administration = provider.GetRequiredService<ISettingsAdministration>();

        var result = await administration.ApplyAsync(
            [SettingChange.Set(RetentionSettingDefinitions.FailedCommandRetention.Key, "10675199.02:48:05", 0)],
            Guid.NewGuid());

        Assert.True(result.IsFailure);
        Assert.Equal("settings.above_maximum", Assert.Single(result.Errors).Code);

        await using var scope = provider.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<OperationsDbContext>();
        Assert.Equal(0, await dbContext.Settings.AsNoTracking().CountAsync());
    }

    /// <summary>A zero-padded numeric literal must not reach the audit table's bounded column.</summary>
    [Fact]
    public async Task An_oversized_batch_size_literal_is_rejected_at_gate_a_and_writes_nothing()
    {
        await using var provider = await OperationsTestHost.CreateAsync(
            "cinomni_test_settings_administration_batch_size_length", services => { });

        var administration = provider.GetRequiredService<ISettingsAdministration>();
        var padded = new string('0', 524) + "5000";

        var result = await administration.ApplyAsync(
            [SettingChange.Set(RetentionSettingDefinitions.BatchSize.Key, padded, 0)], Guid.NewGuid());

        Assert.True(result.IsFailure);
        Assert.Equal("settings.too_long", Assert.Single(result.Errors).Code);

        await using var scope = provider.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<OperationsDbContext>();
        Assert.Equal(0, await dbContext.Settings.AsNoTracking().CountAsync());
    }

    /// <summary>A non-integer batch size must not silently degrade to the fallback default at read time.</summary>
    [Fact]
    public async Task A_non_integer_batch_size_is_rejected_at_gate_a()
    {
        await using var provider = await OperationsTestHost.CreateAsync(
            "cinomni_test_settings_administration_batch_size_pattern", services => { });

        var administration = provider.GetRequiredService<ISettingsAdministration>();

        var result = await administration.ApplyAsync(
            [SettingChange.Set(RetentionSettingDefinitions.BatchSize.Key, "7.5", 0)], Guid.NewGuid());

        Assert.True(result.IsFailure);
        Assert.Equal("settings.pattern_mismatch", Assert.Single(result.Errors).Code);
    }

    /// <summary>A batch that would be fine per key but breaks no invariant commits normally.</summary>
    [Fact]
    public async Task A_change_that_satisfies_the_candidate_merged_view_is_applied()
    {
        await using var provider = await OperationsTestHost.CreateAsync(
            "cinomni_test_settings_administration_cross_key_ok", services => { });

        var administration = provider.GetRequiredService<ISettingsAdministration>();

        // 21 days is still comfortably greater than the unchanged 14-day outbox default.
        var result = await administration.ApplyAsync(
            [SettingChange.Set(RetentionSettingDefinitions.CompletedCommandRetention.Key, "21.00:00:00", 0)],
            Guid.NewGuid());

        Assert.True(result.IsSuccess);
        await using var scope = provider.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<OperationsDbContext>();
        var row = await dbContext.Settings.AsNoTracking()
            .SingleAsync(s => s.Key == RetentionSettingDefinitions.CompletedCommandRetention.Key);
        Assert.Equal("21.00:00:00", row.Value);
    }

    private async Task AssertNoRowsAsync()
    {
        await using var scope = _provider.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<OperationsDbContext>();
        Assert.Equal(0, await dbContext.Settings.AsNoTracking().CountAsync());
    }
}
