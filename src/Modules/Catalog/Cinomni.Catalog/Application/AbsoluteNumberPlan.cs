using Cinomni.Catalog.Contracts;
using Cinomni.Catalog.Persistence;

namespace Cinomni.Catalog.Application;

/// <summary>
/// The absolute-number reassignment one snapshot asks for, already split into the two phases the
/// database forces.
/// <para>
/// <c>ux_episodes_work_absolute_number</c> is UNIQUE and <b>not</b> deferrable, so a provider that
/// shifts the whole ordering (a recap special inserted at the front moves E1→2, E2→3, …) cannot be
/// written in one pass: every new value is still held by a neighbour at the moment it is assigned.
/// Refusing the conflicting values instead frees exactly one episode per sync — a 400-episode show
/// would need 400 refresh cycles to converge, and until it did, an anime release numbered by the
/// provider's ordering would resolve to the wrong episode. So the stale claims are
/// <see cref="Released"/> and flushed first, and only then do the new owners take their numbers.
/// </para>
/// </summary>
/// <param name="Released">Episodes whose stored number is claimed by another episode; set to null first.</param>
/// <param name="Assigned">
/// The new owner of each number, applied after the release has been flushed, and whether the number
/// was counted here rather than published by the provider.
/// </param>
internal sealed record AbsoluteNumberPlan(
    IReadOnlyList<Episode> Released,
    IReadOnlyList<(Episode Episode, int Number, bool Derived)> Assigned)
{
    public static readonly AbsoluteNumberPlan Empty = new([], []);

    /// <summary>
    /// Works out who should own which absolute number after <paramref name="structure"/> is applied.
    /// <paramref name="episodesByKey"/> holds every episode of the work — the stored ones plus the ones
    /// this pass created — keyed by the natural key <c>(season number, episode number)</c>.
    /// </summary>
    public static AbsoluteNumberPlan For(
        SeriesStructure structure,
        IReadOnlyDictionary<(int SeasonNumber, int Number), Episode> episodesByKey)
    {
        var owners = OwnersOf(structure, episodesByKey);
        var derived = owners.Count == 0;
        if (derived)
        {
            // The provider published no ordering at all — which is every provider but TheTVDB. Count
            // it instead, so an anime release numbered in absolute terms has something to match
            // against; without this it matches nothing, silently, and the title is simply never
            // acquired while the search that found it reports success.
            owners = CountedOwners(episodesByKey);
        }

        if (owners.Count == 0)
        {
            return Empty;
        }

        var released = new List<Episode>();
        foreach (var episode in episodesByKey.Values)
        {
            // Only a claim another episode is about to take has to go: leaving the rest alone is what
            // keeps "a field is overwritten only when the snapshot supplies a value" true.
            if (episode.AbsoluteNumber is { } stored &&
                owners.TryGetValue(stored, out var newOwner) &&
                !ReferenceEquals(newOwner, episode))
            {
                released.Add(episode);
            }
        }

        var assigned = owners
            .Where(pair => pair.Value.AbsoluteNumber != pair.Key || pair.Value.AbsoluteNumberIsDerived != derived)
            .Select(pair => (pair.Value, pair.Key, derived))
            .ToList();

        return new AbsoluteNumberPlan(released, assigned);
    }

    /// <summary>
    /// The episode the snapshot wants for each absolute number. First mention wins: a snapshot that
    /// names one number twice is provider noise, and the unique index would reject the second write.
    /// </summary>
    private static Dictionary<int, Episode> OwnersOf(
        SeriesStructure structure,
        IReadOnlyDictionary<(int SeasonNumber, int Number), Episode> episodesByKey)
    {
        var owners = new Dictionary<int, Episode>();
        foreach (var input in structure.Episodes)
        {
            if (input.AbsoluteNumber is { } value &&
                episodesByKey.TryGetValue((input.SeasonNumber, input.Number), out var episode))
            {
                owners.TryAdd(value, episode);
            }
        }

        return owners;
    }

    /// <summary>
    /// The ordering counted from the season structure: every regular season in order, every episode
    /// within it in order, numbered from one.
    /// <para>
    /// Specials are excluded, which is the universal convention — season zero holds recaps, OVAs and
    /// shorts that no absolute ordering counts, and including them would shift every real episode.
    /// </para>
    /// <para>
    /// All or nothing, by construction: this runs only when the provider published no number for any
    /// episode. Filling the gaps of a partially numbered series would invent numbers that collide
    /// with the published ones, and the unique index would then reject a legitimate sync.
    /// </para>
    /// </summary>
    private static Dictionary<int, Episode> CountedOwners(
        IReadOnlyDictionary<(int SeasonNumber, int Number), Episode> episodesByKey)
    {
        var owners = new Dictionary<int, Episode>();
        var next = 1;

        foreach (var episode in episodesByKey.Values
            .Where(episode => episode.SeasonNumber > 0)
            .OrderBy(episode => episode.SeasonNumber)
            .ThenBy(episode => episode.Number))
        {
            owners[next++] = episode;
        }

        return owners;
    }
}
