using System.Globalization;
using System.Text.RegularExpressions;

namespace Cinomni.ReleaseParsing.Parsing;

/// <summary>
/// Reads the air date out of a date-numbered release (<c>The.Daily.Show.2024.03.01</c>), the form
/// daily and talk shows use instead of SxxEyy.
/// </summary>
/// <remarks>
/// This must be consulted <b>before</b> the year search: <c>2024.03.01</c> starts with a perfectly
/// valid 20xx token, so a year-first parse would read <c>Year = 2024</c> and collapse every episode
/// of a whole year onto one canonical key. The month/day alternations are exact (01-12 / 01-31) and
/// the pattern refuses a trailing digit, which is what keeps <c>2019.11.1080p</c> from being read
/// as 11 November — there "10" would be the day and "80p" would follow it.
/// </remarks>
internal static class DateEpisodeDetector
{
    // "_" is a regex word character, so a leading \b would never fire on "Show_2024.03.01".
    private static readonly Regex AirDate = ParserRegex.Compile(
        @"(?<![a-z0-9])(19|20)(\d{2})[.\-_](0[1-9]|1[0-2])[.\-_](0[1-9]|[12]\d|3[01])(?!\d)");

    /// <summary>Reads a date-based episode number, or <see langword="null"/> when the name carries none.</summary>
    public static NumberingMatch? Detect(string name)
    {
        var match = AirDate.Match(name);
        if (!match.Success)
        {
            return null;
        }

        var year = Number(match.Groups[1]) * 100 + Number(match.Groups[2]);
        var month = Number(match.Groups[3]);
        var day = Number(match.Groups[4]);

        // 2024.02.31 matches the pattern but is not a date; such a name carries no air date.
        if (day > DateTime.DaysInMonth(year, month))
        {
            return null;
        }

        return new NumberingMatch(
            NumberingMatch.Empty with { AirDate = new DateOnly(year, month, day) },
            match.Index,
            match.Length);
    }

    private static int Number(Group group) =>
        int.Parse(group.Value, NumberStyles.Integer, CultureInfo.InvariantCulture);
}
