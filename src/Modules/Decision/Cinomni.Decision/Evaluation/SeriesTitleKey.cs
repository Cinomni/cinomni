using System.Globalization;
using System.Text;

namespace Cinomni.Decision.Evaluation;

/// <summary>
/// The comparison key behind "is this release the series that was asked for?": a normalized token
/// sequence that two spellings of the <em>same</em> name collapse onto, and two <em>different</em>
/// names never do.
/// <para>
/// Matching is exact on that sequence. It deliberately is not a prefix, a substring or a
/// longest-common-prefix score: every spin-off, sequel, reboot and regional remake is named by
/// appending to — or truncating — the parent's title ("Star Trek" → "Star Trek Picard", "The Walking
/// Dead" → "The Walking Dead World Beyond", "Shameless" → "Shameless US"), and Decision is the only
/// stage of the pipeline that checks identity at all. A partial rule therefore does not degrade
/// gracefully: it hands a stranger's episode to the requested work, where it is hardlinked under the
/// requested episode's name, marked available and never searched for again.
/// </para>
/// <para>
/// Four tolerances survive, and each of them is a difference in <em>writing</em> one name rather
/// than a different name. Each is pinned by <c>SeriesTitleIdentityTests</c>:
/// <list type="number">
///   <item><description>
///     <b>Punctuation and diacritics.</b> Folded away, so "Pokémon" matches "Pokemon" and
///     "Marvel's Daredevil" matches "Marvel.s.Daredevil". A possessive apostrophe is punctuation on
///     one side and a letter on the other ("Bobs.Burgers"), so a bare <c>s</c> token is folded back
///     into the word before it — a standalone "s" is never a title word in its own right.
///   </description></item>
///   <item><description>
///     <b>A leading article.</b> Scene names drop "The" often enough ("Walking.Dead.S01E01") that
///     keeping it would reject the show itself. Two shows that differ only by a leading article are
///     not a case that occurs; two shows that differ by a trailing word are the norm, which is why
///     only the <em>leading</em> article goes.
///   </description></item>
///   <item><description>
///     <b>An interior "and".</b> "&amp;" is punctuation and disappears in the fold while the
///     spelled-out word survives, so "Law &amp; Order" and "Law.and.Order" would otherwise never
///     meet. Only interior occurrences go, so a title that opens or closes on the word keeps it.
///   </description></item>
///   <item><description>
///     <b>A trailing year.</b> The catalog disambiguates a reboot as "Doctor Who (2005)" while the
///     release parser lifts the same year out of the release name into <c>ParsedRelease.Year</c>,
///     so comparing it as a <em>word</em> would reject the very release it identifies. It is
///     compared as a year instead, by <c>EpisodeMatcher</c>. A title that <em>is</em> a year
///     ("1923") keeps it, because a key may never be emptied.
///   </description></item>
/// </list>
/// </para>
/// <para>
/// What is deliberately <b>not</b> tolerated is a region or edition qualifier the other side lacks
/// ("US", "UK", "Uncut"). That is precisely how a remake is named apart from its original, and no
/// downstream stage would catch the confusion. The cost is real — a release legitimately titled
/// "The.Office.US" is rejected for a catalog work titled "The Office" — and it is the right cost for
/// an automated pipeline: a false rejection is one persisted, readable reason on the explainability
/// endpoint, while a false acceptance is a wrong file in the library and a goal closed forever. The
/// principled fix is an alternate-titles table on the catalog work, which does not exist yet.
/// </para>
/// </summary>
internal static class SeriesTitleKey
{
    /// <summary>Articles dropped from the front of a title. English only — the catalog is not localized.</summary>
    private static readonly string[] LeadingArticles = ["the", "a", "an"];

    private const string PossessiveSuffix = "s";
    private const string Connective = "and";
    private const int YearTokenLength = 4;
    private const int EarliestYear = 1900;
    private const int LatestYear = 2999;

    /// <summary>The comparison tokens of a title, in order. Empty only for an empty title.</summary>
    public static string[] Of(string title) =>
        DropTrailingYear(DropInteriorConnectives(DropLeadingArticle(MergePossessives(Fold(title)))));

    /// <summary>
    /// Whether two keys name the same series. An empty key never matches: a release the parser could
    /// extract no title from is not evidence of anything.
    /// </summary>
    public static bool Equal(string[] left, string[] right) =>
        left.Length > 0 && right.Length > 0 && left.SequenceEqual(right, StringComparer.Ordinal);

    /// <summary>The key as it is persisted on the evaluation reason, so a rejection can be read back.</summary>
    public static string Describe(string[] key) => key.Length == 0 ? "none" : string.Join(' ', key);

    /// <summary>
    /// Lower-cased alphanumeric words with diacritics folded away. Applied to <em>both</em> sides:
    /// the release side arrives already folded by the parser (this pass is idempotent on it) and the
    /// requested side is a raw catalog title, which may well carry accents and punctuation.
    /// </summary>
    private static string[] Fold(string title)
    {
        var decomposed = title.Normalize(NormalizationForm.FormD);
        var builder = new StringBuilder(decomposed.Length);
        foreach (var c in decomposed)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.NonSpacingMark)
            {
                continue;
            }

            builder.Append(char.IsLetterOrDigit(c) ? char.ToLowerInvariant(c) : ' ');
        }

        return builder.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries);
    }

    /// <summary>Folds a bare possessive <c>s</c> back into the word it belongs to.</summary>
    private static string[] MergePossessives(string[] tokens)
    {
        if (tokens.Length < 2)
        {
            return tokens;
        }

        var merged = new List<string>(tokens.Length);
        foreach (var token in tokens)
        {
            if (merged.Count > 0 && string.Equals(token, PossessiveSuffix, StringComparison.Ordinal))
            {
                merged[^1] += PossessiveSuffix;
                continue;
            }

            merged.Add(token);
        }

        return [.. merged];
    }

    private static string[] DropLeadingArticle(string[] tokens) =>
        tokens.Length > 1 && LeadingArticles.Contains(tokens[0], StringComparer.Ordinal)
            ? tokens[1..]
            : tokens;

    private static string[] DropInteriorConnectives(string[] tokens)
    {
        if (tokens.Length < 3)
        {
            return tokens;
        }

        var kept = new List<string>(tokens.Length) { tokens[0] };
        for (var i = 1; i < tokens.Length - 1; i++)
        {
            if (!string.Equals(tokens[i], Connective, StringComparison.Ordinal))
            {
                kept.Add(tokens[i]);
            }
        }

        kept.Add(tokens[^1]);
        return [.. kept];
    }

    private static string[] DropTrailingYear(string[] tokens) =>
        tokens.Length > 1 && IsYear(tokens[^1]) ? tokens[..^1] : tokens;

    private static bool IsYear(string token) =>
        token.Length == YearTokenLength
        && int.TryParse(token, NumberStyles.None, CultureInfo.InvariantCulture, out var year)
        && year is >= EarliestYear and <= LatestYear;
}
