using System.Security.Cryptography;
using System.Text;
using Konscious.Security.Cryptography;

namespace Cinomni.Identity.Security;

/// <summary>Hashes and verifies passwords. The hash embeds its own parameters (rehashable).</summary>
public interface IPasswordHasher
{
    string Hash(string password);

    bool Verify(string password, string phcHash);
}

/// <summary>
/// argon2id password hasher. Produces a PHC string
/// <c>$argon2id$v=19$m=..,t=..,p=..$salt$hash</c> so cost parameters travel with the hash and
/// can be raised over time without a schema change.
/// </summary>
public sealed class Argon2idPasswordHasher : IPasswordHasher
{
    // OWASP baseline for argon2id (2024): 19 MiB memory, 2 iterations, 1 lane.
    private const int MemoryKib = 19456;
    private const int Iterations = 2;
    private const int Parallelism = 1;
    private const int SaltBytes = 16;
    private const int HashBytes = 32;
    private const int Argon2Version = 19;

    public string Hash(string password)
    {
        var salt = RandomNumberGenerator.GetBytes(SaltBytes);
        var hash = Derive(password, salt, MemoryKib, Iterations, Parallelism, HashBytes);

        return string.Create(
            System.Globalization.CultureInfo.InvariantCulture,
            $"$argon2id$v={Argon2Version}$m={MemoryKib},t={Iterations},p={Parallelism}${Base64UrlNoPad(salt)}${Base64UrlNoPad(hash)}");
    }

    public bool Verify(string password, string phcHash)
    {
        if (!TryParse(phcHash, out var parameters))
        {
            return false;
        }

        var (memory, iterations, parallelism, salt, expected) = parameters;
        var actual = Derive(password, salt, memory, iterations, parallelism, expected.Length);
        return CryptographicOperations.FixedTimeEquals(actual, expected);
    }

    private static byte[] Derive(string password, byte[] salt, int memoryKib, int iterations, int parallelism, int length)
    {
        using var argon2 = new Argon2id(Encoding.UTF8.GetBytes(password))
        {
            Salt = salt,
            MemorySize = memoryKib,
            Iterations = iterations,
            DegreeOfParallelism = parallelism,
        };

        return argon2.GetBytes(length);
    }

    private static bool TryParse(
        string phcHash,
        out (int Memory, int Iterations, int Parallelism, byte[] Salt, byte[] Hash) parameters)
    {
        parameters = default;

        // $argon2id$v=19$m=..,t=..,p=..$salt$hash  ->  ["", "argon2id", "v=19", "m=..,t=..,p=..", salt, hash]
        var parts = phcHash.Split('$');
        if (parts.Length != 6 || parts[1] != "argon2id")
        {
            return false;
        }

        var costs = parts[3].Split(',');
        if (costs.Length != 3
            || !TryReadInt(costs[0], "m=", out var memory)
            || !TryReadInt(costs[1], "t=", out var iterations)
            || !TryReadInt(costs[2], "p=", out var parallelism))
        {
            return false;
        }

        try
        {
            parameters = (memory, iterations, parallelism, FromBase64UrlNoPad(parts[4]), FromBase64UrlNoPad(parts[5]));
            return true;
        }
        catch (FormatException)
        {
            return false;
        }
    }

    private static bool TryReadInt(string token, string prefix, out int value)
    {
        value = 0;
        return token.StartsWith(prefix, StringComparison.Ordinal)
            && int.TryParse(token.AsSpan(prefix.Length), out value);
    }

    private static string Base64UrlNoPad(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static byte[] FromBase64UrlNoPad(string value)
    {
        var padded = value.Replace('-', '+').Replace('_', '/');
        padded = (padded.Length % 4) switch
        {
            2 => padded + "==",
            3 => padded + "=",
            _ => padded,
        };

        return Convert.FromBase64String(padded);
    }
}
