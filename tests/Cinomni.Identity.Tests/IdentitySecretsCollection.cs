namespace Cinomni.Identity.Tests;

/// <summary>
/// Serializes the test classes that set CINOMNI_SECRET_KEY. The master key is process-wide
/// environment state, so two classes changing it at once would each see the other half of the
/// change — a flake that looks like a cryptography bug and is not one.
/// </summary>
[CollectionDefinition(Serial, DisableParallelization = true)]
public sealed class IdentitySecretsCollection
{
    public const string Serial = "identity-secrets";
}
