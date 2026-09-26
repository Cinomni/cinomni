using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Cinomni.Operations.Settings;

/// <summary>
/// The startup half of the master-key contract: logs the one warning an installation running without
/// <see cref="SecretMasterKey.EnvironmentVariableName"/> needs, and nothing else — the process itself
/// already started normally, because <see cref="SettingsSecretCipher"/> degrades rather than throws for
/// a missing key. Mirrors <c>Cinomni.Host.BackupStartup.ReportBackupContract</c>'s shape: a warning is
/// not a crash, and an installation that never stores a secret setting must not be told it has a problem.
/// </summary>
public static class SettingsSecretsStartup
{
    public static void ReportSecretsContract(IServiceProvider services, ILogger logger)
    {
        var cipher = services.GetRequiredService<SettingsSecretCipher>();
        if (cipher.IsAvailable)
        {
            return;
        }

        logger.LogWarning(
            "{Reason} Secret settings cannot be written until it is set, and any already stored will "
            + "read as unreadable — each affected provider degrades exactly as it does with an empty "
            + "key today.",
            cipher.UnavailableReason);
    }
}
