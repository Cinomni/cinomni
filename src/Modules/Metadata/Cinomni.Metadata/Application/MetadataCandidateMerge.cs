using System.Text;
using Cinomni.Metadata.Contracts;

namespace Cinomni.Metadata.Application;

/// <summary>
/// De-duplicates search candidates across providers. A series search fans out to
/// three providers and, without this, returns the same show three times with no way for the operator to
/// tell the rows apart.
/// <para>
/// Two candidates are the same work when they share <b>any</b> external id (TheTVDB, IMDb or TMDB) — that
/// is the reliable signal, and it is why the adapters cross-reference each other's id spaces. When no id
/// is shared the fallback is a normalised title plus a year, and the year is <b>required</b>: matching on
/// title alone would merge unrelated shows that happen to share a name.
/// </para>
/// <para>
/// The surviving row is the first one seen, and the caller feeds candidates in provider-priority order,
/// so the highest-priority provider's title/overview/external id win. The external ids of every merged
/// row are unioned into the survivor — a TVMaze hit contributing TheTVDB's id is what lets a third
/// provider merge with a row it shares no id with directly.
/// </para>
/// </summary>
internal static class MetadataCandidateMerge
{
    /// <summary>
    /// Merges <paramref name="candidates"/>, preserving the order they arrive in. External-id matching
    /// always applies; <paramref name="mergeByTitleAndYear"/> additionally enables the heuristic fallback,
    /// which the caller turns on for a series only.
    /// </summary>
    public static IReadOnlyList<MetadataCandidate> Merge(
        IReadOnlyList<MetadataCandidate> candidates,
        bool mergeByTitleAndYear = true)
    {
        if (candidates.Count < 2)
        {
            return candidates;
        }

        var merged = new List<MetadataCandidate>(candidates.Count);
        var byKey = new Dictionary<string, int>(StringComparer.Ordinal);

        foreach (var candidate in candidates)
        {
            var keys = KeysOf(candidate, mergeByTitleAndYear);
            var index = FirstMatch(keys, byKey);
            if (index is null)
            {
                merged.Add(candidate);
                Register(keys, merged.Count - 1, byKey);
                continue;
            }

            // Keep the higher-priority row, but adopt every id the duplicate knew — including the ones
            // that made no match, so a later candidate sharing only those still merges here.
            merged[index.Value] = Union(merged[index.Value], candidate);
            Register(KeysOf(merged[index.Value], mergeByTitleAndYear), index.Value, byKey);
        }

        return merged;
    }

    private static int? FirstMatch(IReadOnlyList<string> keys, Dictionary<string, int> byKey)
    {
        foreach (var key in keys)
        {
            if (byKey.TryGetValue(key, out var index))
            {
                return index;
            }
        }

        return null;
    }

    private static void Register(IReadOnlyList<string> keys, int index, Dictionary<string, int> byKey)
    {
        foreach (var key in keys)
        {
            byKey[key] = index;
        }
    }

    private static MetadataCandidate Union(MetadataCandidate survivor, MetadataCandidate duplicate) => survivor with
    {
        TvdbId = survivor.TvdbId ?? duplicate.TvdbId,
        ImdbId = survivor.ImdbId ?? duplicate.ImdbId,
        TmdbId = survivor.TmdbId ?? duplicate.TmdbId,
    };

    private static IReadOnlyList<string> KeysOf(MetadataCandidate candidate, bool mergeByTitleAndYear)
    {
        var keys = new List<string>(4);
        Append(keys, "tvdb", candidate.TvdbId);
        Append(keys, "imdb", candidate.ImdbId);
        Append(keys, "tmdb", candidate.TmdbId);

        // The fallback needs a year: two providers agreeing on both a normalised title and a release year
        // is a safe merge, while title alone is not.
        if (mergeByTitleAndYear && candidate.Year is { } year)
        {
            keys.Add($"title:{Normalize(candidate.Title)}:{year}");
        }

        return keys;
    }

    private static void Append(List<string> keys, string space, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            keys.Add($"{space}:{value.Trim().ToLowerInvariant()}");
        }
    }

    /// <summary>Lower-cases and drops every non-alphanumeric character, so "Marvel's Daredevil" and "Marvels Daredevil" agree.</summary>
    private static string Normalize(string title)
    {
        var builder = new StringBuilder(title.Length);
        foreach (var character in title)
        {
            if (char.IsLetterOrDigit(character))
            {
                builder.Append(char.ToLowerInvariant(character));
            }
        }

        return builder.ToString();
    }
}
