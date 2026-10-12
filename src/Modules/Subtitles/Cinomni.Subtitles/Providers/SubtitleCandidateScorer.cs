using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Cinomni.Subtitles.Providers;

/// <summary>
/// What a provider says a candidate subtitle belongs to, beyond its release name: the identity it
/// published (OpenSubtitles does, per file), and how far it is to be trusted. Every member past the
/// release name is optional; a provider fills what it knows.
/// </summary>
/// <param name="Title">The film's title, or for an episode the series' title.</param>
/// <param name="ImdbId">The IMDb id of the film, or of the episode itself.</param>
/// <param name="ParentImdbId">For an episode, the IMDb id of its series.</param>
public sealed record CandidateEvidence(
    string Release,
    string? Title = null,
    int? Year = null,
    string? ImdbId = null,
    string? ParentImdbId = null,
    int? Season = null,
    int? Episode = null,
    int DownloadCount = 0,
    bool Trusted = false,
    bool MachineTranslated = false);

/// <summary>A candidate's score and, in a sentence, why: kept with the candidate so a choice can be explained.</summary>
public sealed record CandidateScore(int Score, string Reason);

/// <summary>
/// Scores a subtitle candidate against the title it is wanted for.
/// <para>
/// Identity comes first and is not a matter of degree. A subtitle for another film, another series or
/// another episode is worth nothing however popular it is, so it scores 0 and says which. A candidate
/// whose identity the provider confirms by IMDb id starts at <see cref="ConfirmedScore"/>; one that
/// matches by title, year and episode number starts at <see cref="MatchedScore"/>; one with nothing to
/// confirm it starts below the default threshold, so it is never taken on popularity alone. Only then
/// does the rest count: the same release group, source and resolution as the video (whose timing it
/// most likely shares), how often it has been downloaded, a trusted uploader, and a heavy penalty for a
/// machine translation.
/// </para>
/// <para>
/// A provider's own rating was the whole score before, and most files have none: every candidate scored
/// 0, nothing cleared the threshold, and with the threshold lowered the first one returned won — a
/// Hebrew file labelled English, or the subtitle of another film that happened to share a word.
/// </para>
/// </summary>
public static partial class SubtitleCandidateScorer
{
    public const int ConfirmedScore = 60;
    public const int MatchedScore = 45;
    public const int UnconfirmedScore = 25;

    /// <summary>The most a candidate whose identity nothing confirms can score: below the default threshold.</summary>
    public const int UnconfirmedCeiling = 30;

    /// <summary>What a machine translation costs: enough to sink it below any human one.</summary>
    public const int MachineTranslationPenalty = 30;

    private const int GroupBonus = 10;
    private const int SourceBonus = 6;
    private const int ResolutionBonus = 2;
    private const int SharedWordsBonus = 4;
    private const int PopularityBonusCap = 6;
    private const int TrustedBonus = 3;

    public static CandidateScore Score(SubtitleProviderQuery query, CandidateEvidence candidate)
    {
        var (identity, why) = query.IsEpisode ? EpisodeIdentity(query, candidate) : MovieIdentity(query, candidate);
        if (identity == 0)
        {
            return new CandidateScore(0, why);
        }

        var reasons = new List<string> { why };
        var score = identity;
        score += ReleaseLikeness(query.Release, candidate.Release, reasons);

        var popularity = Math.Min(PopularityBonusCap, (int)Math.Round(2 * Math.Log10(Math.Max(0, candidate.DownloadCount) + 1)));
        if (popularity > 0)
        {
            score += popularity;
            reasons.Add(string.Create(CultureInfo.InvariantCulture, $"downloaded {candidate.DownloadCount} times"));
        }

        if (candidate.Trusted)
        {
            score += TrustedBonus;
            reasons.Add("trusted uploader");
        }

        if (candidate.MachineTranslated)
        {
            score -= MachineTranslationPenalty;
            reasons.Add("machine translated");
        }

        // Nothing short of a matched identity may reach the threshold, whatever the bonuses add up to.
        var ceiling = identity == UnconfirmedScore ? UnconfirmedCeiling : 100;
        return new CandidateScore(Math.Clamp(score, 1, ceiling), string.Join("; ", reasons) + ".");
    }

    private static (int Score, string Why) MovieIdentity(SubtitleProviderQuery query, CandidateEvidence candidate)
    {
        var wanted = ImdbNumber(query.ImdbId);
        var theirs = ImdbNumber(candidate.ImdbId);
        if (wanted is not null && theirs is not null)
        {
            return wanted == theirs
                ? (ConfirmedScore, $"Same film by IMDb id (tt{wanted})")
                : (0, $"Another film: {Describe(candidate)} (IMDb tt{theirs}, not tt{wanted})");
        }

        var wantedTitle = query.Title;
        var wantedYear = query.Year;
        if (string.IsNullOrWhiteSpace(wantedTitle))
        {
            return (UnconfirmedScore, "Not confirmed: the film's title is not known to compare with");
        }

        if (candidate.Year is { } year && wantedYear is { } expected && Math.Abs(year - expected) > 1)
        {
            return (0, string.Create(CultureInfo.InvariantCulture, $"Another film: {Describe(candidate)}, not {wantedTitle} ({expected})"));
        }

        var titled = candidate.Title is { Length: > 0 } title
            ? SameTitle(wantedTitle, title)
            : ReleaseNames(candidate.Release, wantedTitle);
        return titled
            ? (MatchedScore, "Same film by title" + (candidate.Year is not null && wantedYear is not null ? " and year" : string.Empty))
            : (0, $"Another film: {Describe(candidate)}, not {wantedTitle}");
    }

    private static (int Score, string Why) EpisodeIdentity(SubtitleProviderQuery query, CandidateEvidence candidate)
    {
        var (season, episode) = candidate.Season is not null && candidate.Episode is not null
            ? (candidate.Season, candidate.Episode)
            : Numbering(candidate.Release);
        var wanted = EpisodeLabel(query.SeasonNumber, query.EpisodeNumber);
        if (season is null || episode is null)
        {
            return (0, $"Not confirmed: it does not say which episode it is ({wanted} wanted)");
        }

        if (season != query.SeasonNumber || episode != query.EpisodeNumber)
        {
            return (0, $"Another episode: {EpisodeLabel(season, episode)}, not {wanted}");
        }

        var wantedSeries = ImdbNumber(query.ParentImdbId);
        var theirSeries = ImdbNumber(candidate.ParentImdbId);
        if (wantedSeries is not null && theirSeries is not null)
        {
            return wantedSeries == theirSeries
                ? (ConfirmedScore, $"Same episode {wanted} of the same series by IMDb id (tt{wantedSeries})")
                : (0, $"Another series: {candidate.Title ?? candidate.Release} (IMDb tt{theirSeries}, not tt{wantedSeries})");
        }

        if (string.IsNullOrWhiteSpace(query.SeriesTitle))
        {
            return (UnconfirmedScore, $"Episode {wanted}, but the series is not known to compare with");
        }

        var sameSeries = candidate.Title is { Length: > 0 } title
            ? SameTitle(query.SeriesTitle, title)
            : ReleaseNames(candidate.Release, query.SeriesTitle);
        return sameSeries
            ? (MatchedScore, $"Same episode {wanted} of {query.SeriesTitle} by title")
            : (0, $"Another series: {candidate.Title ?? candidate.Release}, not {query.SeriesTitle}");
    }

    /// <summary>What the candidate's release name shares with the video's: likely the same encode, so the same timing.</summary>
    private static int ReleaseLikeness(string video, string candidate, List<string> reasons)
    {
        var bonus = 0;
        if (Group(video) is { } group && string.Equals(group, Group(candidate), StringComparison.OrdinalIgnoreCase))
        {
            bonus += GroupBonus;
            reasons.Add($"same release group {group}");
        }

        var videoWords = Words(video);
        var candidateWords = Words(candidate);
        if (SourceOf(videoWords) is { } source && source == SourceOf(candidateWords))
        {
            bonus += SourceBonus;
            reasons.Add($"same source ({source})");
        }

        if (ResolutionOf(videoWords) is { } resolution && resolution == ResolutionOf(candidateWords))
        {
            bonus += ResolutionBonus;
            reasons.Add($"same resolution ({resolution})");
        }

        if (videoWords.Count > 0 && candidateWords.Count > 0)
        {
            var shared = videoWords.Intersect(candidateWords).Count();
            var all = videoWords.Union(candidateWords).Count();
            bonus += (int)Math.Round(SharedWordsBonus * (double)shared / all);
        }

        return bonus;
    }

    private static string Describe(CandidateEvidence candidate) =>
        candidate.Title is { Length: > 0 } title
            ? candidate.Year is { } year ? string.Create(CultureInfo.InvariantCulture, $"{title} ({year})") : title
            : candidate.Release;

    private static string EpisodeLabel(int? season, int? episode) =>
        string.Create(CultureInfo.InvariantCulture, $"S{season:00}E{episode:00}");

    /// <summary>The number in an IMDb title id (<c>tt0898266</c>, <c>898266</c>), or null for anything else.</summary>
    internal static string? ImdbNumber(string? imdbId)
    {
        if (string.IsNullOrWhiteSpace(imdbId))
        {
            return null;
        }

        var digits = imdbId.Trim();
        digits = digits.StartsWith("tt", StringComparison.OrdinalIgnoreCase) ? digits[2..] : digits;
        if (digits.Length == 0 || !digits.All(char.IsAsciiDigit))
        {
            return null;
        }

        var number = digits.TrimStart('0');
        return number.Length == 0 ? null : number;
    }

    /// <summary>
    /// Whether two titles name the same thing once case, accents, punctuation, a leading article and a
    /// trailing year are set aside: <c>The Big Bang Theory</c> and <c>Big Bang Theory</c>,
    /// <c>Doctor Who (2005)</c> and <c>Doctor Who</c>.
    /// </summary>
    internal static bool SameTitle(string wanted, string candidate)
    {
        var a = CanonicalTitle(wanted);
        var b = CanonicalTitle(candidate);
        return a.Length > 0 && a == b;
    }

    /// <summary>Whether a release name carries the title as a run of whole words, anywhere in it.</summary>
    internal static bool ReleaseNames(string release, string title)
    {
        var titleWords = CanonicalTitle(title).Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var releaseWords = Normalize(release).Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (titleWords.Length == 0)
        {
            return false;
        }

        for (var start = 0; start + titleWords.Length <= releaseWords.Length; start++)
        {
            if (titleWords.Select((word, i) => releaseWords[start + i] == word).All(same => same))
            {
                return true;
            }
        }

        return false;
    }

    private static string CanonicalTitle(string title)
    {
        var words = Normalize(title).Split(' ', StringSplitOptions.RemoveEmptyEntries).ToList();
        if (words.Count > 1 && words[^1].Length == 4 && YearWord().IsMatch(words[^1]))
        {
            words.RemoveAt(words.Count - 1);
        }

        if (words.Count > 1 && words[0] is "the" or "a" or "an")
        {
            words.RemoveAt(0);
        }

        return string.Join(' ', words);
    }

    /// <summary>Lowercase, accents removed, <c>&amp;</c> as "and", apostrophes dropped, everything else a space.</summary>
    private static string Normalize(string text)
    {
        var decomposed = text.Replace("&", " and ", StringComparison.Ordinal).Normalize(NormalizationForm.FormD);
        var builder = new StringBuilder(decomposed.Length);
        foreach (var c in decomposed)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.NonSpacingMark || c is '\'' or '’')
            {
                continue;
            }

            builder.Append(char.IsLetterOrDigit(c) ? char.ToLowerInvariant(c) : ' ');
        }

        return string.Join(' ', builder.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries));
    }

    private static (int? Season, int? Episode) Numbering(string release)
    {
        Match match;
        try
        {
            match = EpisodeNumbering().Match(release);
        }
        catch (RegexMatchTimeoutException)
        {
            // A release name is provider text: one built to stall the pattern says nothing either.
            return (null, null);
        }

        if (!match.Success)
        {
            return (null, null);
        }

        var season = match.Groups["s1"].Success ? match.Groups["s1"].Value : match.Groups["s2"].Value;
        var episode = match.Groups["e1"].Success ? match.Groups["e1"].Value : match.Groups["e2"].Value;
        return (int.Parse(season, CultureInfo.InvariantCulture), int.Parse(episode, CultureInfo.InvariantCulture));
    }

    private static HashSet<string> Words(string release) =>
        Normalize(release).Split(' ', StringSplitOptions.RemoveEmptyEntries).ToHashSet(StringComparer.Ordinal);

    /// <summary>The release group: what follows the last hyphen, when it is one plain word.</summary>
    private static string? Group(string release)
    {
        var dash = release.LastIndexOf('-');
        if (dash < 0 || dash == release.Length - 1)
        {
            return null;
        }

        var group = release[(dash + 1)..].Trim();
        var bracket = group.IndexOfAny(['[', '(', ' ', '.']);
        group = bracket > 0 ? group[..bracket] : group;
        return group.Length is >= 2 and <= 20 && group.All(char.IsLetterOrDigit) ? group : null;
    }

    private static string? SourceOf(HashSet<string> words) =>
        words.Overlaps(["bluray", "bdrip", "brrip", "bdremux", "remux", "bd"]) ? "BluRay"
        : words.Overlaps(["web", "webrip", "webdl", "amzn", "nf", "dsnp", "hmax", "atvp", "hulu"]) ? "WEB"
        : words.Overlaps(["hdtv", "pdtv", "dsr"]) ? "HDTV"
        : words.Overlaps(["dvdrip", "dvd", "dvd5", "dvd9"]) ? "DVD"
        : null;

    private static string? ResolutionOf(HashSet<string> words) =>
        new[] { "2160p", "1080p", "720p", "576p", "480p" }.FirstOrDefault(words.Contains);

    [GeneratedRegex(@"(?:\b[Ss](?<s1>\d{1,2})[ ._-]?[Ee](?<e1>\d{1,3})\b)|(?:\b(?<s2>\d{1,2})x(?<e2>\d{2,3})\b)", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 200)]
    private static partial Regex EpisodeNumbering();

    [GeneratedRegex(@"^(19|20)\d{2}$", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 200)]
    private static partial Regex YearWord();
}
