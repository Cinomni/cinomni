using System.Text.RegularExpressions;

namespace Cinomni.ReleaseParsing.Parsing;

/// <summary>Detects a movie edition (Director's Cut, Extended, IMAX, …) from the tag portion.</summary>
internal static class EditionDetector
{
    private static readonly Regex Edition = ParserRegex.Compile(
        @"\b(directors?[. _]?cut|final[. _]?cut|extended([. _]?(cut|edition))?|special[. _]?edition"
        + @"|ultimate[. _]?edition|anniversary[. _]?edition|collectors[. _]?edition|unrated|uncut|imax"
        + @"|remastered|criterion|theatrical([. _]?cut)?|redux)\b");

    public static string? Detect(string scope)
    {
        var match = Edition.Match(scope);
        if (!match.Success)
        {
            return null;
        }

        var words = match.Value
            .Replace('.', ' ')
            .Replace('_', ' ')
            .Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Select(w => char.ToUpperInvariant(w[0]) + w[1..].ToLowerInvariant());

        return string.Join(' ', words);
    }
}
