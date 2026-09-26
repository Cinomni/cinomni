namespace Cinomni.Catalog.Contracts;

/// <summary>
/// The age-classification ladders this installation will enforce. One ordered list per region,
/// least restrictive first. A label that is not on the list is not ranked: a work carrying one stays
/// visible, and a ceiling that is not on the current region's list does not filter.
/// <para>
/// Movie and television labels for the same region share one ladder, interleaved by how restrictive
/// each label is published as. That interleaving is a judgment — <c>TV-14</c> sits above <c>PG-13</c> —
/// and it is written here so a later change is a diff rather than a surprise. A region with no list
/// is refused at write time rather than guessed: a wrong ladder does not fail, it hides the wrong titles.
/// </para>
/// </summary>
public static class ContentRatingScale
{
    private static readonly IReadOnlyDictionary<string, IReadOnlyList<RankedLabel>> Ladders =
        new Dictionary<string, IReadOnlyList<RankedLabel>>(StringComparer.OrdinalIgnoreCase)
        {
            ["US"] = Rank(
                ("TV-Y", 0), ("TV-Y7", 0), ("TV-G", 0), ("G", 0),
                ("TV-PG", 1), ("PG", 1),
                ("PG-13", 2),
                ("TV-14", 3),
                ("R", 4),
                ("TV-MA", 5), ("NC-17", 5)),
            // Cinema and television labels together, as the provider publishes them: "Ai" and "7i" are
            // the "especially recommended for children" variants of A and 7, "X" is the adult-film
            // classification, and 10 and 13 are television-only. A label missing here is not ranked,
            // so it stays visible under every ceiling — "X" did.
            ["ES"] = Rank(
                ("A", 0), ("Ai", 0), ("APTA", 0), ("TP", 0), ("Infantil", 0),
                ("7", 1), ("7i", 1),
                ("10", 2),
                ("12", 3),
                ("13", 4),
                ("16", 5),
                ("18", 6),
                ("X", 7)),
            ["DE"] = Rank(("0", 0), ("6", 1), ("12", 2), ("16", 3), ("18", 4)),
            ["GB"] = Rank(("U", 0), ("PG", 1), ("12A", 2), ("12", 2), ("15", 3), ("18", 4), ("R18", 5)),
        };

    /// <summary>Whether this installation can order classifications for <paramref name="region"/>.</summary>
    public static bool HasLadder(string? region) => TryLadder(region, out _);

    /// <summary>The certificates an administrator may set as a ceiling, least restrictive first.</summary>
    public static IReadOnlyList<string> Certificates(string? region) =>
        TryLadder(region, out var ladder)
            ? ladder.Select(label => label.Label).Distinct(StringComparer.Ordinal).ToList()
            : [];

    /// <summary>The ladder's own spelling of a certificate, or false when this region does not list it.</summary>
    public static bool TryCanonical(string? region, string? certificate, out string canonical)
    {
        canonical = string.Empty;
        if (!TryLadder(region, out var ladder) || string.IsNullOrWhiteSpace(certificate))
        {
            return false;
        }

        foreach (var label in ladder)
        {
            if (string.Equals(label.Label, certificate.Trim(), StringComparison.OrdinalIgnoreCase))
            {
                canonical = label.Label;
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Whether a stored ceiling is the one the current region can enforce. A region change does not
    /// reinterpret yesterday's certificate under today's ladder.
    /// </summary>
    public static bool Applies(string? currentRegion, string? ceiling, string? ceilingRegion) =>
        !string.IsNullOrWhiteSpace(ceiling)
        && !string.IsNullOrWhiteSpace(ceilingRegion)
        && !string.IsNullOrWhiteSpace(currentRegion)
        && string.Equals(currentRegion.Trim(), ceilingRegion.Trim(), StringComparison.OrdinalIgnoreCase)
        && TryCanonical(currentRegion, ceiling, out _);

    /// <summary>
    /// Certificates strictly above the ceiling. Empty when the ceiling does not apply, which is also
    /// what "hide nothing" looks like to a query.
    /// </summary>
    public static IReadOnlyList<string> Above(string? currentRegion, string? ceiling, string? ceilingRegion)
    {
        if (!Applies(currentRegion, ceiling, ceilingRegion) || !TryLadder(currentRegion, out var ladder))
        {
            return [];
        }

        var ceilingRank = ladder.First(label =>
            string.Equals(label.Label, ceiling, StringComparison.OrdinalIgnoreCase)).Rank;
        return ladder
            .Where(label => label.Rank > ceilingRank)
            .Select(label => label.Label)
            .Distinct(StringComparer.Ordinal)
            .ToList();
    }

    private static bool TryLadder(string? region, out IReadOnlyList<RankedLabel> ladder)
    {
        if (region is not null && Ladders.TryGetValue(region.Trim(), out var found))
        {
            ladder = found;
            return true;
        }

        ladder = [];
        return false;
    }

    private static IReadOnlyList<RankedLabel> Rank(params (string Label, int Rank)[] labels) =>
        labels.Select(label => new RankedLabel(label.Label, label.Rank)).ToList();

    private readonly record struct RankedLabel(string Label, int Rank);
}
