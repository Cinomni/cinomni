using System.Text;
using Cinomni.Identity.Security;

namespace Cinomni.Identity.Tests;

/// <summary>
/// The one-time password primitive, pinned against the vectors the standards publish. This is the
/// right way to test an algorithm nobody should be inventing: if these pass, an authenticator
/// application will agree with us, and no amount of reading the code proves that as well.
/// </summary>
public sealed class TotpTests
{
    /// <summary>The RFC 6238 Appendix B seed for HMAC-SHA1: the ASCII digits, twenty bytes of them.</summary>
    private static byte[] RfcSeed => Encoding.ASCII.GetBytes("12345678901234567890");

    [Theory]
    // RFC 6238 Appendix B, reduced to the six digits Cinomni issues — the published values are the
    // eight-digit form, and the six-digit code is its last six digits by construction.
    [InlineData(59L, "287082")]
    [InlineData(1111111109L, "081804")]
    [InlineData(1111111111L, "050471")]
    [InlineData(1234567890L, "005924")]
    [InlineData(2000000000L, "279037")]
    [InlineData(20000000000L, "353130")]
    public void Generates_the_codes_rfc6238_publishes(long unixSeconds, string expected)
    {
        var counter = unixSeconds / (long)Totp.Step.TotalSeconds;

        Assert.Equal(expected, Totp.Generate(RfcSeed, counter));
    }

    [Theory]
    // RFC 4648 section 10, without the padding this encoder deliberately does not emit.
    [InlineData("f", "MY")]
    [InlineData("fo", "MZXQ")]
    [InlineData("foo", "MZXW6")]
    [InlineData("foob", "MZXW6YQ")]
    [InlineData("fooba", "MZXW6YTB")]
    [InlineData("foobar", "MZXW6YTBOI")]
    public void Encodes_base32_the_way_rfc4648_says(string plain, string expected)
    {
        var encoded = Base32.Encode(Encoding.ASCII.GetBytes(plain));

        Assert.Equal(expected, encoded);
        Assert.True(Base32.TryDecode(encoded, out var round));
        Assert.Equal(Encoding.ASCII.GetBytes(plain), round);
    }

    [Fact]
    public void Decodes_a_secret_a_person_retyped()
    {
        // Case and separators are what somebody copying a displayed secret gets wrong, and neither
        // changes which secret it is.
        Assert.True(Base32.TryDecode("mzxw6ytboi", out var lower));
        Assert.True(Base32.TryDecode("MZXW-6YTB OI", out var spaced));

        Assert.Equal(Encoding.ASCII.GetBytes("foobar"), lower);
        Assert.Equal(lower, spaced);
    }

    [Theory]
    [InlineData("MZXW6YTB!")] // outside the alphabet
    [InlineData("MZXW60TB")]  // '0' is not a base32 digit, and mistaking it for 'O' would be a guess
    [InlineData("")]
    public void Refuses_a_secret_it_cannot_read_rather_than_guessing(string encoded)
    {
        // Skipping an unknown character would let two different strings decode to one secret, which
        // is the sort of leniency that quietly widens what counts as a valid credential.
        Assert.False(Base32.TryDecode(encoded, out _));
    }

    [Fact]
    public void Accepts_a_code_from_the_step_before_and_after_but_no_further()
    {
        var secret = Base32.Encode(RfcSeed);
        var now = DateTimeOffset.FromUnixTimeSeconds(1111111111);

        Assert.True(Totp.IsValid(secret, "050471", now));
        // A person typing at the moment the step rolls over, and one whose clock is half a minute out.
        Assert.True(Totp.IsValid(secret, "081804", now));
        Assert.True(Totp.IsValid(secret, Totp.Generate(RfcSeed, (1111111111 / 30) + 1), now));
        // Two steps away is not drift, and accepting it would double what a guess is worth.
        Assert.False(Totp.IsValid(secret, Totp.Generate(RfcSeed, (1111111111 / 30) + 2), now));
        Assert.False(Totp.IsValid(secret, Totp.Generate(RfcSeed, (1111111111 / 30) - 2), now));
    }

    [Theory]
    [InlineData("12345")]    // too short
    [InlineData("1234567")]  // too long
    [InlineData("05047a")]
    [InlineData("")]
    public void Refuses_anything_that_is_not_six_digits(string code)
    {
        Assert.False(Totp.IsValid(Base32.Encode(RfcSeed), code, DateTimeOffset.FromUnixTimeSeconds(1111111111)));
    }

    [Fact]
    public void The_enrollment_uri_says_everything_an_authenticator_needs()
    {
        var uri = Totp.EnrollmentUri("Cinomni", "ada", "MZXW6YTBOI");

        // Spelled out rather than left to defaults: an application that assumes SHA-256 or eight
        // digits would generate codes this installation rejects, and the user would have no idea why.
        Assert.StartsWith("otpauth://totp/Cinomni%3Aada?", uri, StringComparison.Ordinal);
        Assert.Contains("secret=MZXW6YTBOI", uri, StringComparison.Ordinal);
        Assert.Contains("issuer=Cinomni", uri, StringComparison.Ordinal);
        Assert.Contains("algorithm=SHA1", uri, StringComparison.Ordinal);
        Assert.Contains("digits=6", uri, StringComparison.Ordinal);
        Assert.Contains("period=30", uri, StringComparison.Ordinal);
    }

    [Fact]
    public void A_fresh_secret_is_twenty_bytes_and_its_own()
    {
        var first = Totp.CreateSecret();
        var second = Totp.CreateSecret();

        Assert.NotEqual(first, second);
        Assert.True(Base32.TryDecode(first, out var bytes));
        Assert.Equal(Totp.SecretBytes, bytes.Length);
    }
}
