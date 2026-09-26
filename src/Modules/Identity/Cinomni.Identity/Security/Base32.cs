namespace Cinomni.Identity.Security;

/// <summary>
/// RFC 4648 base32, the alphabet every authenticator application expects a TOTP secret in. The BCL
/// has base64 and hex and not this, so it is thirty lines here rather than a dependency.
/// <para>
/// Decoding is deliberately forgiving about the two things a person retyping a secret gets wrong —
/// case, and the padding or spacing an application displayed it with — and unforgiving about
/// everything else. A character outside the alphabet is a refusal, not a skip: silently ignoring it
/// would let two different strings decode to the same secret.
/// </para>
/// </summary>
internal static class Base32
{
    private const string Alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";

    public static string Encode(ReadOnlySpan<byte> bytes)
    {
        var encoded = new System.Text.StringBuilder((bytes.Length * 8 + 4) / 5);
        var buffer = 0;
        var bits = 0;

        foreach (var value in bytes)
        {
            buffer = (buffer << 8) | value;
            bits += 8;
            while (bits >= 5)
            {
                bits -= 5;
                encoded.Append(Alphabet[(buffer >> bits) & 0x1F]);
            }
        }

        if (bits > 0)
        {
            encoded.Append(Alphabet[(buffer << (5 - bits)) & 0x1F]);
        }

        return encoded.ToString();
    }

    public static bool TryDecode(string encoded, out byte[] bytes)
    {
        bytes = [];
        var decoded = new List<byte>(encoded.Length * 5 / 8);
        var buffer = 0;
        var bits = 0;

        foreach (var character in encoded)
        {
            if (character is '=' or ' ' or '-')
            {
                continue;
            }

            var index = Alphabet.IndexOf(char.ToUpperInvariant(character), StringComparison.Ordinal);
            if (index < 0)
            {
                return false;
            }

            buffer = (buffer << 5) | index;
            bits += 5;
            if (bits >= 8)
            {
                bits -= 8;
                decoded.Add((byte)((buffer >> bits) & 0xFF));
            }
        }

        if (decoded.Count == 0)
        {
            return false;
        }

        bytes = [.. decoded];
        return true;
    }
}
