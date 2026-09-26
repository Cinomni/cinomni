using System.Text.RegularExpressions;
using Cinomni.ReleaseParsing.Contracts;

namespace Cinomni.ReleaseParsing.Parsing;

/// <summary>
/// The single numbering vocabulary of the platform (<see cref="IEpisodeNumberParser"/>): it
/// composes the three detectors into one <see cref="EpisodeNumbering"/>. Import resolves media
/// <b>file</b> names through this type and <see cref="ReleaseParser"/> reuses the very same scan
/// for release names, so no episode regex is ever duplicated.
/// </summary>
/// <remarks>
/// Pure, static and stateless (only compiled regexes), therefore safe as a DI singleton under the
/// parallel indexer fan-out. It never throws: an empty, over-long, unrecognised or pathological
/// name yields <see langword="null"/>, and a regex timeout is caught and reported the same way.
/// </remarks>
public sealed class EpisodeNumberParser : IEpisodeNumberParser
{
    /// <inheritdoc />
    public EpisodeNumbering? Parse(string name) => Scan(name)?.Numbering;

    /// <summary>
    /// Scans <paramref name="name"/> and returns the numbering together with the span it occupies,
    /// which is what the release parser needs to cut the title and tag scopes around it.
    /// </summary>
    internal static NumberingMatch? Scan(string name)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Length > NumberingLimits.MaxNameLength)
        {
            return null;
        }

        try
        {
            return ScanCore(name);
        }
        catch (RegexMatchTimeoutException)
        {
            // A pathological name carries no numbering; it must never surface as an exception.
            return null;
        }
    }

    private static NumberingMatch? ScanCore(string name)
    {
        // Season/episode first (it is the most specific), then the date form, then anime absolutes.
        // A name that already carries SxxEyy must never be re-read as a date or an absolute number.
        var primary = SeriesNumberingDetector.Detect(name)
            ?? DateEpisodeDetector.Detect(name)
            ?? AbsoluteEpisodeDetector.Detect(name);

        var completeSpan = SeriesNumberingDetector.DetectCompleteSeries(name);
        var isComplete = completeSpan is not null
            || (primary is not null && SeriesNumberingDetector.HasBareComplete(name));
        var part = SeriesNumberingDetector.DetectPart(name);

        if (primary is null && !isComplete && part is null)
        {
            return null;
        }

        var numbering = (primary?.Numbering ?? NumberingMatch.Empty) with
        {
            IsComplete = isComplete,
            Part = part,
        };

        // The scope-cutting span is the primary numbering's, falling back to the complete-series
        // marker. "Part 2" deliberately has no span: for a movie it belongs to the title, and
        // cutting there would hide the release year behind it.
        if (primary is not null)
        {
            return new NumberingMatch(numbering, primary.Start, primary.Length);
        }

        return completeSpan is (int start, int length)
            ? new NumberingMatch(numbering, start, length)
            : new NumberingMatch(numbering, Start: -1, Length: 0);
    }
}
