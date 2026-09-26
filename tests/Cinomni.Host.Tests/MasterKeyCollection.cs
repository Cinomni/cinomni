namespace Cinomni.Host.Tests;

/// <summary>
/// Serializes every test class that reads or writes <c>CINOMNI_SECRET_KEY</c>. The master key is
/// process-wide state read once, when <c>AddOperations</c> composes the cipher, so a class that sets
/// one cannot run beside a class that clears it. Mirrors <c>IndexerSecretsCollection</c> in the
/// Discovery tests and <c>SettingsSecretsCollection</c> in the Operations tests, which exist for the
/// same reason and are not visible from this assembly.
/// </summary>
[CollectionDefinition(Serial, DisableParallelization = true)]
public sealed class MasterKeyCollection
{
    public const string Serial = "host-master-key";
}
