using Cinomni.Identity.Security;
using Xunit;

namespace Cinomni.Identity.Tests;

/// <summary>Unit tests for the argon2id password hasher (no database).</summary>
public sealed class PasswordHasherTests
{
    private readonly Argon2idPasswordHasher _hasher = new();

    [Fact]
    public void Hash_produces_an_argon2id_phc_string()
    {
        var hash = _hasher.Hash("correct horse battery staple");

        Assert.StartsWith("$argon2id$v=19$", hash);
    }

    [Fact]
    public void Verify_accepts_the_correct_password()
    {
        var hash = _hasher.Hash("s3cret-passphrase");

        Assert.True(_hasher.Verify("s3cret-passphrase", hash));
    }

    [Fact]
    public void Verify_rejects_a_wrong_password()
    {
        var hash = _hasher.Hash("s3cret-passphrase");

        Assert.False(_hasher.Verify("not-the-password", hash));
    }

    [Fact]
    public void Hashing_the_same_password_twice_yields_different_hashes()
    {
        var first = _hasher.Hash("same-password");
        var second = _hasher.Hash("same-password");

        // Different random salt each time; both still verify.
        Assert.NotEqual(first, second);
        Assert.True(_hasher.Verify("same-password", first));
        Assert.True(_hasher.Verify("same-password", second));
    }

    [Fact]
    public void Verify_returns_false_for_a_malformed_hash()
    {
        Assert.False(_hasher.Verify("whatever", "not-a-phc-string"));
    }
}
