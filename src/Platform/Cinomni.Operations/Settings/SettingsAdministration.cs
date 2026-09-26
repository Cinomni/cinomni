using Cinomni.Kernel.Identifiers;
using Cinomni.Operations.Persistence;
using Cinomni.Operations.Transactions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;

namespace Cinomni.Operations.Settings;

/// <summary>
/// The write path, in the order the design fixes: resolve every key against the code-defined catalogue;
/// refuse a key an environment variable or the command line already pins; validate the batch (gate a per
/// key, then gates b/c against the candidate merged view); encrypt secret values outside the transaction;
/// commit every row and its audit entry in one transaction with per-key optimistic concurrency; publish
/// the new snapshot only after the commit. A rejected batch writes nothing and never changes
/// <see cref="SettingsCache.Current"/>.
/// </summary>
public sealed class SettingsAdministration(
    OperationsDbContext dbContext,
    IUnitOfWork unitOfWork,
    SettingsCache cache,
    SettingsSecretCipher cipher,
    IConfiguration configuration,
    IEnumerable<SettingDefinition> definitions,
    IEnumerable<SettingsCheckRegistration> checks,
    TimeProvider timeProvider)
    : ISettingsAdministration
{
    public async Task<SettingsApplyResult> ApplyAsync(
        IReadOnlyList<SettingChange> changes, Guid actorId, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(changes);
        if (changes.Count == 0)
        {
            return SettingsApplyResult.Success;
        }

        var catalogue = definitions.ToDictionary(d => d.Key, StringComparer.Ordinal);

        // Step 1: resolve every key. Nothing is written for a batch naming even one key this build has
        // no definition for.
        var unknown = changes
            .Where(c => !catalogue.ContainsKey(c.Key))
            .Select(c => new SettingError([c.Key], "settings.unknown_key", $"'{c.Key}' is not a settable key."))
            .ToList();
        if (unknown.Count > 0)
        {
            return SettingsApplyResult.Failure(unknown);
        }

        // Step 2: a key pinned by the environment or the command line cannot be changed here — a write
        // that persisted but had no effect is exactly the mystery precedence exists to prevent.
        var pinned = changes
            .Where(c => SettingsPrecedence.IsPinned(configuration, catalogue[c.Key].ConfigurationPath))
            .Select(c => new SettingError(
                [c.Key],
                "settings.overridden_by_environment",
                $"'{c.Key}' is set by the environment or the command line and cannot be changed here. "
                + "Remove the override there to manage it in the settings store."))
            .ToList();
        if (pinned.Count > 0)
        {
            return SettingsApplyResult.Failure(pinned);
        }

        // Step 3a: gate (a), per key, as data.
        var perKeyErrors = changes
            .SelectMany(change => SettingDefinitionValidator.Validate(catalogue[change.Key], change))
            .ToList();
        if (perKeyErrors.Count > 0)
        {
            return SettingsApplyResult.Failure(perKeyErrors);
        }

        // Step 3b/c: gates (b) and (c) against the candidate merged view — current values with this
        // batch's pending changes applied — so a cross-key invariant is judged against what the
        // installation would become, not against each key in isolation.
        var candidateView = new SettingsView(BuildCandidateSnapshot(cache.Current, changes), configuration);
        var changedKeys = changes.Select(c => c.Key).ToHashSet(StringComparer.Ordinal);
        var crossKeyErrors = checks
            .Where(group => group.Keys.Any(changedKeys.Contains))
            .SelectMany(group => group.Evaluate(candidateView))
            .ToList();
        if (crossKeyErrors.Count > 0)
        {
            return SettingsApplyResult.Failure(crossKeyErrors);
        }

        // Fast-path optimistic-concurrency check before opening the transaction: a stale version is the
        // common case (a human editing a panel another tab already changed) and needs no rollback to
        // detect. Setting.Version is also mapped as an EF concurrency token, so a genuine race that slips
        // past this read is still caught — see the catch below — by the UPDATE/DELETE's own WHERE clause.
        var staleKeys = await FindStaleKeysAsync(changes, cancellationToken);
        if (staleKeys.Count > 0)
        {
            return ConflictResult(staleKeys);
        }

        // Step 4: encrypt secret values. Pure CPU, so it runs before the transaction opens and keeps the
        // transaction short.
        var encrypted = new Dictionary<string, EncryptedSecret>(StringComparer.Ordinal);
        var encryptErrors = new List<SettingError>();
        foreach (var change in changes.Where(c => c.Kind == SettingChangeKind.Set && catalogue[c.Key].IsSecret))
        {
            var result = cipher.Encrypt(change.Key, change.Value ?? string.Empty);
            if (result.IsFailure)
            {
                encryptErrors.Add(new SettingError([change.Key], result.Error.Code, result.Error.Message));
                continue;
            }

            encrypted[change.Key] = result.Value;
        }
        if (encryptErrors.Count > 0)
        {
            return SettingsApplyResult.Failure(encryptErrors);
        }

        // Step 5: one transaction. Every row and its audit entry commit together or not at all.
        try
        {
            await unitOfWork.ExecuteAsync(
                ct => WriteChangesAsync(changes, catalogue, encrypted, actorId, ct), cancellationToken);
        }
        catch (DbUpdateConcurrencyException ex)
        {
            // The backstop: something changed a targeted row between the read above and this commit.
            // Narrowed to the concurrency subtype specifically — any other DbUpdateException (a
            // constraint violation, a truncated column) is a distinct storage fault, not a race with
            // another writer, and must not be reported as the same 409 conflict.
            //
            // The change tracker may still hold the failed entities after the rollback (the
            // UnitOfWork only discards module-context state, not its own owner context), so they are
            // cleared here rather than risking a later, unrelated SaveChangesAsync in the same scope
            // re-flushing them.
            dbContext.ChangeTracker.Clear();
            return ConflictResult(ExtractConflictedKeys(ex, changes));
        }

        // Step 6: publish only after the commit. A process that dies between commit and this line is
        // recoverable — the next poll or the next start reloads the same committed rows.
        var snapshot = await SettingsSnapshotLoader.LoadAsync(dbContext, cipher, cache.Current.Generation + 1, cancellationToken);
        cache.Publish(snapshot);

        return SettingsApplyResult.Success;
    }

    private async Task<IReadOnlyList<string>> FindStaleKeysAsync(
        IReadOnlyList<SettingChange> changes, CancellationToken cancellationToken)
    {
        var stale = new List<string>();
        foreach (var change in changes)
        {
            var existing = await dbContext.Settings.FindAsync([change.Key], cancellationToken);
            var currentVersion = existing?.Version ?? 0;
            if (currentVersion != change.ExpectedVersion)
            {
                stale.Add(change.Key);
            }
        }

        return stale;
    }

    private async Task WriteChangesAsync(
        IReadOnlyList<SettingChange> changes,
        IReadOnlyDictionary<string, SettingDefinition> catalogue,
        IReadOnlyDictionary<string, EncryptedSecret> encrypted,
        Guid actorId,
        CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();

        foreach (var change in changes)
        {
            var definition = catalogue[change.Key];

            // Reuses the same tracked instance FindStaleKeysAsync already loaded (EF's identity map),
            // so this is a local lookup rather than a second round trip.
            var existing = await dbContext.Settings.FindAsync([change.Key], cancellationToken);
            var oldDisplay = existing is null ? null : DisplayFor(definition, existing);

            if (change.Kind == SettingChangeKind.Clear)
            {
                if (existing is null)
                {
                    // Already absent: clearing an unset key is a no-op, nothing to audit.
                    continue;
                }

                dbContext.Settings.Remove(existing);
                dbContext.SettingAudits.Add(NewAudit(change.Key, now, actorId, SettingAuditAction.Cleared, oldDisplay, null));
                continue;
            }

            string newDisplay;
            if (definition.IsSecret)
            {
                var secret = encrypted[change.Key];
                newDisplay = secret.Fingerprint;

                if (existing is null)
                {
                    dbContext.Settings.Add(new Setting
                    {
                        Key = change.Key,
                        SecretCipher = secret.Cipher,
                        SecretNonce = secret.Nonce,
                        KeyId = secret.KeyId,
                        Fingerprint = secret.Fingerprint,
                        Version = 1,
                        UpdatedAt = now,
                        UpdatedBy = actorId,
                    });
                }
                else
                {
                    existing.Value = null;
                    existing.SecretCipher = secret.Cipher;
                    existing.SecretNonce = secret.Nonce;
                    existing.KeyId = secret.KeyId;
                    existing.Fingerprint = secret.Fingerprint;
                    existing.Version += 1;
                    existing.UpdatedAt = now;
                    existing.UpdatedBy = actorId;
                }
            }
            else
            {
                var value = change.Value ?? string.Empty;
                newDisplay = value;

                if (existing is null)
                {
                    dbContext.Settings.Add(new Setting
                    {
                        Key = change.Key,
                        Value = value,
                        Version = 1,
                        UpdatedAt = now,
                        UpdatedBy = actorId,
                    });
                }
                else
                {
                    existing.Value = value;
                    existing.SecretCipher = null;
                    existing.SecretNonce = null;
                    existing.KeyId = null;
                    existing.Fingerprint = null;
                    existing.Version += 1;
                    existing.UpdatedAt = now;
                    existing.UpdatedBy = actorId;
                }
            }

            dbContext.SettingAudits.Add(NewAudit(change.Key, now, actorId, SettingAuditAction.Set, oldDisplay, newDisplay));
        }

        await dbContext.SaveChangesAsync(cancellationToken);
    }

    private static SettingsSnapshot BuildCandidateSnapshot(SettingsSnapshot current, IReadOnlyList<SettingChange> changes)
    {
        var builder = current.Values.ToBuilder();
        foreach (var change in changes)
        {
            if (change.Kind == SettingChangeKind.Clear)
            {
                builder.Remove(change.Key);
            }
            else
            {
                builder[change.Key] = change.Value ?? string.Empty;
            }
        }

        // The generation is never published from a candidate snapshot; it only backs a transient
        // SettingsView for the duration of this validation pass.
        return current with { Values = builder.ToImmutable() };
    }

    private static string? DisplayFor(SettingDefinition definition, Setting row) =>
        definition.IsSecret ? row.Fingerprint : row.Value;

    private static SettingAudit NewAudit(
        string key, DateTimeOffset now, Guid actorId, string action, string? oldDisplay, string? newDisplay) =>
        new()
        {
            Id = Uuid7.New(now),
            Key = key,
            ChangedAt = now,
            ChangedBy = actorId,
            Action = action,
            OldDisplay = oldDisplay,
            NewDisplay = newDisplay,
        };

    private static IReadOnlyList<string> ExtractConflictedKeys(DbUpdateException ex, IReadOnlyList<SettingChange> changes)
    {
        var keys = ex.Entries
            .Select(e => e.Entity)
            .OfType<Setting>()
            .Select(s => s.Key)
            .Distinct(StringComparer.Ordinal)
            .ToList();

        return keys.Count > 0 ? keys : changes.Select(c => c.Key).ToList();
    }

    private static SettingsApplyResult ConflictResult(IReadOnlyList<string> keys) =>
        SettingsApplyResult.Failure(
        [
            new SettingError(
                keys,
                "settings.conflict",
                $"These settings were changed by another writer since they were last read: "
                + $"{string.Join(", ", keys)}. Reload and try again."),
        ]);
}
