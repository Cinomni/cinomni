using System.Security.Cryptography;

namespace Cinomni.Identity.Security;

/// <summary>
/// Time-based one-time passwords, RFC 6238 over RFC 4226. Implemented here rather than taken from a
/// package: it is one HMAC and a truncation, the standard is short and stable, and the RFC publishes
/// test vectors that pin the implementation exactly — which is a better guarantee than a dependency
/// whose provenance would have to be recorded and reviewed for thirty lines of arithmetic.
/// <para>
/// SHA-1 is not a lapse. RFC 6238 defines HMAC-SHA1 as the default and it is what every authenticator
/// application implements; the security of a one-time password rests on the shared secret and the
/// thirty-second window, not on collision resistance, and choosing a stronger hash here would produce
/// codes no common authenticator can generate.
/// </para>
/// </summary>
internal static class Totp
{
    /// <summary>The RFC 6238 default, and what every authenticator assumes when the URI omits it.</summary>
    internal static readonly TimeSpan Step = TimeSpan.FromSeconds(30);

    internal const int Digits = 6;

    /// <summary>
    /// How many steps either side of now are accepted. One step — thirty seconds back and forward —
    /// covers the clock drift and the typing delay a person actually has, and costs three valid codes
    /// out of a million rather than one. Widening it multiplies what a guess is worth; the attempt cap
    /// on a login challenge is what makes that bound meaningful.
    /// </summary>
    internal const int WindowSteps = 1;

    /// <summary>160 bits, the RFC 4226 recommendation, and what a 32-character base32 secret carries.</summary>
    internal const int SecretBytes = 20;

    /// <summary>A fresh shared secret, base32 as every authenticator expects to be given it.</summary>
    public static string CreateSecret() => Base32.Encode(RandomNumberGenerator.GetBytes(SecretBytes));

    /// <summary>
    /// Whether <paramref name="code"/> is valid for <paramref name="base32Secret"/> at
    /// <paramref name="now"/>, within <see cref="WindowSteps"/>.
    /// <para>
    /// Every accepted step is compared in fixed time and the loop always runs to the end, so the reply
    /// carries no information about <em>which</em> step matched or how close a wrong code came.
    /// </para>
    /// </summary>
    public static bool IsValid(string base32Secret, string code, DateTimeOffset now) =>
        TryMatch(base32Secret, code, now, out _);

    /// <summary>
    /// <see cref="IsValid"/>, also naming the time step the code belongs to. A code is valid for its
    /// whole window, so accepting it once is not enough: the caller records <paramref name="step"/> and
    /// refuses any code whose step is not later, or the same six digits sign in twice.
    /// </summary>
    public static bool TryMatch(string base32Secret, string code, DateTimeOffset now, out long step)
    {
        step = 0;
        if (code.Length != Digits || !code.All(char.IsAsciiDigit))
        {
            return false;
        }

        if (!Base32.TryDecode(base32Secret, out var secret))
        {
            return false;
        }

        var counter = now.ToUnixTimeSeconds() / (long)Step.TotalSeconds;
        var matched = false;
        for (var offset = -WindowSteps; offset <= WindowSteps; offset++)
        {
            var expected = Generate(secret, counter + offset);
            var equal = CryptographicOperations.FixedTimeEquals(
                System.Text.Encoding.ASCII.GetBytes(expected), System.Text.Encoding.ASCII.GetBytes(code));
            step = equal ? counter + offset : step;
            matched |= equal;
        }

        return matched;
    }

    /// <summary>The code for one counter value — RFC 4226 dynamic truncation.</summary>
    internal static string Generate(byte[] secret, long counter)
    {
        Span<byte> message = stackalloc byte[8];
        System.Buffers.Binary.BinaryPrimitives.WriteInt64BigEndian(message, counter);

        Span<byte> digest = stackalloc byte[20];
        HMACSHA1.HashData(secret, message, digest);

        // The low four bits of the last byte choose where to read the four-byte value from, which is
        // what makes the code depend on the whole digest rather than a fixed slice of it.
        var offset = digest[^1] & 0x0F;
        var binary = ((digest[offset] & 0x7F) << 24)
            | (digest[offset + 1] << 16)
            | (digest[offset + 2] << 8)
            | digest[offset + 3];

        return (binary % 1_000_000).ToString("D6", System.Globalization.CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// The <c>otpauth://</c> URI an authenticator reads from a QR code. The issuer appears twice
    /// because that is what the de-facto specification asks for: once as a label prefix, for
    /// applications that only read the label, and once as a parameter for those that do not.
    /// </summary>
    public static string EnrollmentUri(string issuer, string username, string base32Secret)
    {
        var label = Uri.EscapeDataString($"{issuer}:{username}");
        var parameters = string.Join(
            "&",
            $"secret={base32Secret}",
            $"issuer={Uri.EscapeDataString(issuer)}",
            "algorithm=SHA1",
            $"digits={Digits}",
            $"period={(int)Step.TotalSeconds}");

        return $"otpauth://totp/{label}?{parameters}";
    }
}
