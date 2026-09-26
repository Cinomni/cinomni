using System.Collections.Immutable;
using Cinomni.Operations.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Cinomni.Operations.Settings;

/// <summary>
/// Builds a <see cref="SettingsSnapshot"/> from the committed rows of <c>operations.setting</c>.
/// Shared by the startup load and the drift poll, so both take exactly the same view of the table.
/// </summary>
internal static class SettingsSnapshotLoader
{
    public static async Task<SettingsSnapshot> LoadAsync(
        OperationsDbContext dbContext,
        SettingsSecretCipher cipher,
        long generation,
        CancellationToken cancellationToken)
    {
        var rows = await dbContext.Settings
            .AsNoTracking()
            .Select(s => new { s.Key, s.Value, s.SecretCipher, s.SecretNonce, s.KeyId })
            .ToListAsync(cancellationToken);

        var builder = ImmutableDictionary.CreateBuilder<string, string>(StringComparer.Ordinal);
        foreach (var row in rows)
        {
            if (row.Value is not null)
            {
                builder[row.Key] = row.Value;
                continue;
            }

            if (row.SecretCipher is null || row.SecretNonce is null)
            {
                continue;
            }

            // A secret row that cannot be decrypted — no master key loaded, a lost/rotated key, or a
            // tampered row — is treated as absent rather than thrown on, so a database that already has
            // one does not stop this process from starting. The affected provider degrades exactly as it
            // does with an empty key; SettingsSecretsStartup logs the one warning that explains why.
            var read = cipher.Decrypt(row.Key, row.SecretCipher, row.SecretNonce, row.KeyId);
            if (read.IsReadable)
            {
                builder[row.Key] = read.Plaintext!;
            }
        }

        return new SettingsSnapshot(generation, builder.ToImmutable());
    }
}
