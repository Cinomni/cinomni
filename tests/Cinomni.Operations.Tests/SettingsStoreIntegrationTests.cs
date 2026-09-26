using Cinomni.Operations.Persistence;
using Cinomni.Operations.Settings;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Cinomni.Operations.Tests;

/// <summary>
/// Integration tests for the settings store's schema and read path against a real PostgreSQL
/// instance: the <c>operations.setting</c>/<c>operations.setting_audit</c> constraints, the startup
/// loader, and the drift poll.
/// </summary>
public sealed class SettingsStoreIntegrationTests : IAsyncLifetime
{
    private ServiceProvider _provider = null!;

    public async Task InitializeAsync()
    {
        _provider = await OperationsTestHost.CreateAsync("cinomni_test_settings_store", _ => { });
    }

    public async Task DisposeAsync() => await _provider.DisposeAsync();

    [Fact]
    public async Task A_row_with_neither_plaintext_nor_ciphertext_violates_the_check_constraint()
    {
        await using var scope = _provider.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<OperationsDbContext>();

        dbContext.Settings.Add(NewRow("test.neither", value: null, secretCipher: null));

        await Assert.ThrowsAsync<DbUpdateException>(() => dbContext.SaveChangesAsync());
    }

    [Fact]
    public async Task A_row_with_both_plaintext_and_ciphertext_violates_the_check_constraint()
    {
        await using var scope = _provider.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<OperationsDbContext>();

        var row = NewRow("test.both", value: "plaintext", secretCipher: [1, 2, 3]);
        row.SecretNonce = new byte[12];
        dbContext.Settings.Add(row);

        await Assert.ThrowsAsync<DbUpdateException>(() => dbContext.SaveChangesAsync());
    }

    [Fact]
    public async Task A_plaintext_row_and_a_ciphertext_row_both_persist()
    {
        await using var scope = _provider.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<OperationsDbContext>();

        var secretRow = NewRow("test.secret", value: null, secretCipher: [9, 9, 9]);
        secretRow.SecretNonce = new byte[12];
        secretRow.KeyId = "env:aaaaaaaa";
        secretRow.Fingerprint = "bbbbbbbb";

        dbContext.Settings.AddRange(NewRow("test.plain", value: "hello", secretCipher: null), secretRow);
        await dbContext.SaveChangesAsync();

        Assert.Equal(2, await dbContext.Settings.AsNoTracking().CountAsync());
    }

    [Fact]
    public async Task Setting_audit_action_is_restricted_to_set_or_cleared()
    {
        await using var scope = _provider.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<OperationsDbContext>();

        dbContext.SettingAudits.Add(new SettingAudit
        {
            Id = Guid.NewGuid(),
            Key = "test.plain",
            ChangedAt = DateTimeOffset.UtcNow,
            ChangedBy = Guid.NewGuid(),
            Action = "Renamed",
        });

        await Assert.ThrowsAsync<DbUpdateException>(() => dbContext.SaveChangesAsync());
    }

    [Fact]
    public async Task Setting_audit_accepts_set_and_cleared()
    {
        await using var scope = _provider.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<OperationsDbContext>();
        var actor = Guid.NewGuid();

        dbContext.SettingAudits.AddRange(
            new SettingAudit
            {
                Id = Guid.NewGuid(), Key = "test.plain", ChangedAt = DateTimeOffset.UtcNow,
                ChangedBy = actor, Action = SettingAuditAction.Set, NewDisplay = "hello",
            },
            new SettingAudit
            {
                Id = Guid.NewGuid(), Key = "test.plain", ChangedAt = DateTimeOffset.UtcNow,
                ChangedBy = actor, Action = SettingAuditAction.Cleared, OldDisplay = "hello",
            });

        await dbContext.SaveChangesAsync();

        Assert.Equal(2, await dbContext.SettingAudits.AsNoTracking().CountAsync());
    }

    [Fact]
    public async Task Startup_load_against_an_empty_table_publishes_an_empty_snapshot_at_a_positive_generation()
    {
        await _provider.LoadSettingsAsync();

        await using var scope = _provider.CreateAsyncScope();
        var cache = scope.ServiceProvider.GetRequiredService<SettingsCache>();

        Assert.Empty(cache.Current.Values);
        Assert.True(cache.Current.Generation > 0);
    }

    [Fact]
    public async Task Startup_load_reads_plaintext_rows_and_excludes_secret_only_rows()
    {
        await using (var scope = _provider.CreateAsyncScope())
        {
            var dbContext = scope.ServiceProvider.GetRequiredService<OperationsDbContext>();
            var secretRow = NewRow("test.secret", value: null, secretCipher: [1]);
            secretRow.SecretNonce = new byte[12];
            dbContext.Settings.AddRange(NewRow("test.plain", value: "hello", secretCipher: null), secretRow);
            await dbContext.SaveChangesAsync();
        }

        await _provider.LoadSettingsAsync();

        await using var verify = _provider.CreateAsyncScope();
        var cache = verify.ServiceProvider.GetRequiredService<SettingsCache>();

        Assert.Equal("hello", cache.Current.Values["test.plain"]);
        Assert.False(cache.Current.Values.ContainsKey("test.secret"));
    }

    [Fact]
    public async Task First_poll_baselines_without_reloading()
    {
        await _provider.LoadSettingsAsync();

        await using var scope = _provider.CreateAsyncScope();
        var cache = scope.ServiceProvider.GetRequiredService<SettingsCache>();
        var worker = scope.ServiceProvider.GetRequiredService<SettingsRefreshWorker>();
        var dbContext = scope.ServiceProvider.GetRequiredService<OperationsDbContext>();
        var generationBefore = cache.Current.Generation;

        var reloaded = await worker.PollOnceAsync(dbContext, CancellationToken.None);

        Assert.False(reloaded);
        Assert.Equal(generationBefore, cache.Current.Generation);
    }

    [Fact]
    public async Task An_unchanged_table_does_not_bump_the_generation_on_a_later_poll()
    {
        await _provider.LoadSettingsAsync();

        await using var scope = _provider.CreateAsyncScope();
        var cache = scope.ServiceProvider.GetRequiredService<SettingsCache>();
        var worker = scope.ServiceProvider.GetRequiredService<SettingsRefreshWorker>();
        var dbContext = scope.ServiceProvider.GetRequiredService<OperationsDbContext>();

        await worker.PollOnceAsync(dbContext, CancellationToken.None); // baseline
        var generationBefore = cache.Current.Generation;

        var reloaded = await worker.PollOnceAsync(dbContext, CancellationToken.None);

        Assert.False(reloaded);
        Assert.Equal(generationBefore, cache.Current.Generation);
    }

    [Fact]
    public async Task A_row_added_after_the_baseline_is_picked_up_by_the_next_poll()
    {
        await _provider.LoadSettingsAsync();

        await using var scope = _provider.CreateAsyncScope();
        var cache = scope.ServiceProvider.GetRequiredService<SettingsCache>();
        var worker = scope.ServiceProvider.GetRequiredService<SettingsRefreshWorker>();
        var dbContext = scope.ServiceProvider.GetRequiredService<OperationsDbContext>();

        await worker.PollOnceAsync(dbContext, CancellationToken.None); // baseline
        var generationBefore = cache.Current.Generation;

        // Simulates a write this process did not make itself (a psql session, a restore).
        dbContext.Settings.Add(NewRow("test.plain", value: "hello", secretCipher: null));
        await dbContext.SaveChangesAsync();

        var reloaded = await worker.PollOnceAsync(dbContext, CancellationToken.None);

        Assert.True(reloaded);
        Assert.True(cache.Current.Generation > generationBefore);
        Assert.Equal("hello", cache.Current.Values["test.plain"]);
    }

    private static Setting NewRow(string key, string? value, byte[]? secretCipher) => new()
    {
        Key = key,
        Value = value,
        SecretCipher = secretCipher,
        Version = 1,
        UpdatedAt = DateTimeOffset.UtcNow,
        UpdatedBy = Guid.NewGuid(),
    };
}
