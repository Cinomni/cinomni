using Cinomni.Kernel.Identifiers;
using Cinomni.Subtitles.Contracts;

namespace Cinomni.Subtitles.Persistence;

/// <summary>
/// The Subtitles aggregate root: the search for one missing subtitle language on a media asset.
/// Its finite-state machine is pure, guarded logic. A search that finds nothing over the score
/// threshold goes to <see cref="SubtitleSearchState.NotFound"/> and waits for an adaptive re-search
/// rather than failing outright; a downloaded subtitle lands it in <see cref="SubtitleSearchState.Available"/>.
/// Keyed by <c>(asset, language, forced, hi)</c> so a redelivered trigger reuses it.
/// </summary>
public sealed class SubtitleSearch
{
    private const int LanguageMaxLength = 20;

    public Guid Id { get; init; }

    /// <summary>The media asset this subtitle is for (inter-schema reference to library, no physical FK).</summary>
    public Guid AssetId { get; init; }

    public required string Language { get; init; }

    public bool Forced { get; init; }

    public bool HearingImpaired { get; init; }

    public SubtitleSearchState State { get; private set; }

    /// <summary>How many search attempts have run (the adaptive re-search counter).</summary>
    public int Attempts { get; private set; }

    /// <summary>The subtitle that landed, once available.</summary>
    public Guid? SubtitleAssetId { get; private set; }

    public DateTimeOffset CreatedAt { get; init; }

    public DateTimeOffset UpdatedAt { get; private set; }

    public List<SubtitleCandidate> Candidates { get; } = [];

    public bool IsSatisfied => State == SubtitleSearchState.Available;

    public static SubtitleSearch Create(Guid assetId, string language, bool forced, bool hearingImpaired, DateTimeOffset now) => new()
    {
        Id = Uuid7.New(),
        AssetId = assetId,
        Language = Text.Truncate(language, LanguageMaxLength)!,
        Forced = forced,
        HearingImpaired = hearingImpaired,
        State = SubtitleSearchState.Requested,
        CreatedAt = now,
        UpdatedAt = now,
    };

    /// <summary>
    /// Requested/NotFound/Downloading → Searching: opens an attempt. A download that never landed is
    /// reopened on the same backoff as a search that found nothing.
    /// </summary>
    public void BeginSearch(DateTimeOffset now)
    {
        Require(SubtitleSearchState.Requested, SubtitleSearchState.NotFound, SubtitleSearchState.Downloading);
        Attempts += 1;
        Transition(SubtitleSearchState.Searching, now);
    }

    /// <summary>Searching → Evaluated: records the scored candidates the providers returned.</summary>
    public void RecordCandidates(IEnumerable<(string Provider, string Release, int Score, bool Hi, string DownloadRef)> candidates, DateTimeOffset now)
    {
        Require(SubtitleSearchState.Searching);
        foreach (var candidate in candidates)
        {
            Candidates.Add(SubtitleCandidate.Create(
                Id, Candidates.Count, candidate.Provider, candidate.Release, candidate.Score, candidate.Hi, candidate.DownloadRef));
        }

        Transition(SubtitleSearchState.Evaluated, now);
    }

    /// <summary>Evaluated → Downloading: the best candidate cleared the threshold.</summary>
    public void BeginDownload(DateTimeOffset now)
    {
        Require(SubtitleSearchState.Evaluated);
        Transition(SubtitleSearchState.Downloading, now);
    }

    /// <summary>
    /// Searching/Evaluated/Downloading → NotFound: nothing acceptable, or the chosen file did not land.
    /// An adaptive re-search reopens it.
    /// </summary>
    public void MarkNotFound(DateTimeOffset now)
    {
        Require(SubtitleSearchState.Searching, SubtitleSearchState.Evaluated, SubtitleSearchState.Downloading);
        Transition(SubtitleSearchState.NotFound, now);
    }

    /// <summary>Downloading → Available: the subtitle landed on disk and was registered.</summary>
    public void MarkAvailable(Guid subtitleAssetId, DateTimeOffset now)
    {
        Require(SubtitleSearchState.Downloading);
        SubtitleAssetId = subtitleAssetId;
        Transition(SubtitleSearchState.Available, now);
    }

    /// <summary>
    /// Requested → Available without a search: the asset already carries this subtitle — a track in the
    /// file, or one downloaded before. Recording it is what lets the catch-up count the language as
    /// covered; with no row it was "missing" on every pass and never let the backlog behind it through.
    /// </summary>
    public void MarkAlreadyPresent(DateTimeOffset now)
    {
        Require(SubtitleSearchState.Requested);
        Transition(SubtitleSearchState.Available, now);
    }

    private void Require(params SubtitleSearchState[] valid)
    {
        if (Array.IndexOf(valid, State) < 0)
        {
            throw new InvalidOperationException(
                $"Illegal subtitle-search transition from {State} for search {Id} (expected one of {string.Join(",", valid)}).");
        }
    }

    private void Transition(SubtitleSearchState to, DateTimeOffset now)
    {
        State = to;
        UpdatedAt = now;
    }
}
