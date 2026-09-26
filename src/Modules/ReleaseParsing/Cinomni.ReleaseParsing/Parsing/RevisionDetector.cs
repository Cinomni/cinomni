using System.Globalization;
using System.Text.RegularExpressions;
using Cinomni.ReleaseParsing.Contracts;

namespace Cinomni.ReleaseParsing.Parsing;

/// <summary>
/// Detects revision markers (proper / repack / real / vN). Run over the tag portion of the name
/// (after the year) so a title word like "Real Steel" is not mistaken for a REAL scene tag.
/// </summary>
internal static class RevisionDetector
{
    private static readonly Regex Proper = ParserRegex.Compile(@"\bproper\b");
    private static readonly Regex Repack = ParserRegex.Compile(@"\b(repack|rerip)\b");
    private static readonly Regex Real = ParserRegex.Compile(@"\breal\b");
    private static readonly Regex Version = ParserRegex.Compile(@"\bv([2-9])\b");

    public static Revision Detect(string scope)
    {
        var version = 1;

        var explicitVersion = Version.Match(scope);
        if (explicitVersion.Success
            && int.TryParse(explicitVersion.Groups[1].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n))
        {
            version = Math.Max(version, n);
        }

        // Each PROPER bumps the effective version (one PROPER = v2).
        var properCount = Proper.Matches(scope).Count;
        if (properCount > 0)
        {
            version = Math.Max(version, 1 + properCount);
        }

        return new Revision(version, Real: Real.IsMatch(scope), IsRepack: Repack.IsMatch(scope));
    }
}
