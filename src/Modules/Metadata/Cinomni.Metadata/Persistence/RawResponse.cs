using System.Globalization;

namespace Cinomni.Metadata.Persistence;

/// <summary>
/// Guards the size of <c>metadata_snapshots.raw_response</c>. The column is a debugging aid, not a source
/// of truth — every field the platform reads is already a column or a relational child — but it is jsonb,
/// so an unbounded provider payload is stored, indexed-page-split and vacuumed forever.
/// <para>
/// A series embed is where this bites: a long-running show carries hundreds of episodes, per provider,
/// per refresh, and <c>HttpClient.MaxResponseContentBufferSize</c> is 16 MB. The adapters already strip
/// the episode arrays out of what they return; this is the backstop for everything else. An oversized
/// payload is replaced — never truncated — because a truncated string is not valid JSON and the insert
/// would fail the whole unit of work.
/// </para>
/// </summary>
internal static class RawResponse
{
    /// <summary>Largest provider payload stored verbatim. Well under the 16 MB fetch buffer.</summary>
    public const int MaxCharacters = 256 * 1024;

    /// <summary>
    /// The payload, or a small valid-JSON marker recording that it was dropped for size. Always returns
    /// something that parses as jsonb.
    /// </summary>
    public static string Clamp(string raw) =>
        raw.Length <= MaxCharacters
            ? raw
            : $"{{\"elided\":true,\"reason\":\"raw-response-too-large\",\"characters\":{raw.Length.ToString(CultureInfo.InvariantCulture)}}}";
}
