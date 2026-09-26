using Cinomni.Metadata.Contracts;
using Cinomni.Metadata.Providers;

namespace Cinomni.Metadata.Application;

/// <summary>
/// Ranking preferences for artwork selection: an ordered language preference where an empty string means
/// a text-less (language-neutral) image. Lower index wins. Built from the provider language, always
/// falling back through neutral and English.
/// </summary>
public sealed record ArtworkSelectionOptions(IReadOnlyList<string> LanguagePreference)
{
    /// <summary>Preference derived from a BCP-47 language: the language, then text-less, then English.</summary>
    public static ArtworkSelectionOptions ForLanguage(string language)
    {
        var primary = Primary(language);
        var preference = new List<string> { primary, string.Empty };
        if (!string.Equals(primary, "en", StringComparison.OrdinalIgnoreCase))
        {
            preference.Add("en");
        }

        return new ArtworkSelectionOptions(preference);
    }

    private static string Primary(string language) =>
        language.Split('-', StringSplitOptions.RemoveEmptyEntries) is [var first, ..]
            ? first.ToLowerInvariant()
            : language.ToLowerInvariant();
}

/// <summary>
/// The artwork chosen for one scope, one candidate per kind (any may be absent). <see cref="Still"/> is
/// trailing and optional: it only ever applies to an episode scope, so a movie selection is constructed
/// exactly as before.
/// </summary>
public sealed record ArtworkSelection(
    ProviderArtwork? Poster,
    ProviderArtwork? Backdrop,
    ProviderArtwork? Logo,
    ProviderArtwork? Still = null)
{
    public string? PosterUrl => Poster?.Url;

    public string? BackdropUrl => Backdrop?.Url;

    /// <summary>Whether the given candidate instance is the one selected for its kind (reference identity).</summary>
    public bool IsSelected(ProviderArtwork candidate) =>
        ReferenceEquals(candidate, Poster)
        || ReferenceEquals(candidate, Backdrop)
        || ReferenceEquals(candidate, Logo)
        || ReferenceEquals(candidate, Still);
}

/// <summary>
/// The outcome of running the selection policy over a scoped candidate set: which candidate indices are
/// selected (one per kind <i>per scope</i>), plus the poster and backdrop of the <b>series-level</b>
/// scope — the only ones that may be copied onto the snapshot's own urls.
/// </summary>
public sealed record ScopedArtworkSelection(IReadOnlySet<int> SelectedIndices, string? PosterUrl, string? BackdropUrl)
{
    /// <summary>Whether the candidate at <paramref name="index"/> is the selection for its kind and scope.</summary>
    public bool IsSelected(int index) => SelectedIndices.Contains(index);
}

/// <summary>
/// Pure, explainable artwork selection policy: from a provider's candidate set, pick the best
/// poster/backdrop/logo. Ranking within a kind is language preference first (preferred → neutral →
/// English → other), then community vote (average, then count), then resolution — a stable order, so
/// providers' original ordering breaks any remaining tie. No I/O, no state: trivially unit-tested.
/// </summary>
public static class ArtworkSelector
{
    /// <summary>
    /// Picks one candidate per kind from a set that shares a single scope. This is the whole policy; the
    /// series case is the same policy applied once per scope.
    /// </summary>
    public static ArtworkSelection Select(IReadOnlyList<ProviderArtwork> candidates, ArtworkSelectionOptions options) => new(
        Best(candidates, ArtworkKind.Poster, options),
        Best(candidates, ArtworkKind.Backdrop, options),
        Best(candidates, ArtworkKind.Logo, options),
        Best(candidates, ArtworkKind.Still, options));

    /// <summary>
    /// Runs <see cref="Select"/> once per <c>(season, episode)</c> scope rather than once over a flat
    /// mixed list. Running it flat is the season-poster bug: a season poster and the series poster are
    /// both <see cref="ArtworkKind.Poster"/>, so one list yields one winner and a season image can take
    /// the series slot. Only the series-level scope contributes
    /// <see cref="ScopedArtworkSelection.PosterUrl"/>/<see cref="ScopedArtworkSelection.BackdropUrl"/>.
    /// </summary>
    public static ScopedArtworkSelection SelectScoped(IReadOnlyList<ProviderArtwork> candidates, ArtworkSelectionOptions options)
    {
        var selected = new HashSet<int>();
        string? posterUrl = null;
        string? backdropUrl = null;

        var scopes = Enumerable.Range(0, candidates.Count)
            .GroupBy(index => (Season: candidates[index].SeasonNumber, Episode: candidates[index].EpisodeNumber));

        foreach (var scope in scopes)
        {
            var indices = scope.ToList();
            var selection = Select(indices.Select(index => candidates[index]).ToList(), options);
            foreach (var index in indices.Where(index => selection.IsSelected(candidates[index])))
            {
                selected.Add(index);
            }

            if (scope.Key is { Season: null, Episode: null })
            {
                posterUrl = selection.PosterUrl;
                backdropUrl = selection.BackdropUrl;
            }
        }

        return new ScopedArtworkSelection(selected, posterUrl, backdropUrl);
    }

    private static ProviderArtwork? Best(IReadOnlyList<ProviderArtwork> candidates, ArtworkKind kind, ArtworkSelectionOptions options) =>
        candidates
            .Where(c => c.Kind == kind && !string.IsNullOrEmpty(c.Url))
            .OrderBy(c => LanguageRank(c.Language, options.LanguagePreference))
            .ThenByDescending(c => c.VoteAverage ?? double.MinValue)
            .ThenByDescending(c => c.VoteCount ?? int.MinValue)
            .ThenByDescending(Resolution)
            .FirstOrDefault();

    private static int LanguageRank(string? language, IReadOnlyList<string> preference)
    {
        var normalized = language ?? string.Empty;
        for (var i = 0; i < preference.Count; i++)
        {
            if (string.Equals(preference[i], normalized, StringComparison.OrdinalIgnoreCase))
            {
                return i;
            }
        }

        return preference.Count;
    }

    private static long Resolution(ProviderArtwork artwork) => (long)(artwork.Width ?? 0) * (artwork.Height ?? 0);
}
