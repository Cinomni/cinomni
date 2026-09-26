using System.Text.RegularExpressions;

namespace Cinomni.ReleaseParsing.Parsing;

/// <summary>
/// Builds the case-insensitive regexes the detectors use, each with a match timeout. Release
/// names are hostile input (they come from indexers), so every match is bounded in time — a
/// pathological title can never hang a parse (ReDoS defense). Patterns are kept linear (simple
/// alternations, no nested quantifiers) so the timeout is only a backstop.
/// </summary>
internal static class ParserRegex
{
    private static readonly TimeSpan MatchTimeout = TimeSpan.FromMilliseconds(250);

    public static Regex Compile(string pattern) =>
        new(pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, MatchTimeout);
}
