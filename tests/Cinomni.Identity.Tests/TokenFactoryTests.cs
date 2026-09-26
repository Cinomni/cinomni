using Cinomni.Identity.Security;
using Xunit;

namespace Cinomni.Identity.Tests;

/// <summary>Unit tests for opaque session token creation and hashing (no database).</summary>
public sealed class TokenFactoryTests
{
    private readonly TokenFactory _factory = new();

    [Fact]
    public void Create_returns_a_token_whose_hash_matches_Hash()
    {
        var (token, tokenHash) = _factory.Create();

        Assert.False(string.IsNullOrWhiteSpace(token));
        Assert.Equal(tokenHash, _factory.Hash(token));
    }

    [Fact]
    public void Create_produces_unique_tokens()
    {
        var (firstToken, firstHash) = _factory.Create();
        var (secondToken, secondHash) = _factory.Create();

        Assert.NotEqual(firstToken, secondToken);
        Assert.NotEqual(firstHash, secondHash);
    }

    [Fact]
    public void Hash_is_stable_for_the_same_token()
    {
        var (token, _) = _factory.Create();

        Assert.Equal(_factory.Hash(token), _factory.Hash(token));
    }
}
