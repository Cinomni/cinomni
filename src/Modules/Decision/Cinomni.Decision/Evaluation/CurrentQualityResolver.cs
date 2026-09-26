using Cinomni.Decision.Persistence;
using Cinomni.Library.Contracts;
using Cinomni.ReleaseParsing.Contracts;

namespace Cinomni.Decision.Evaluation;

/// <summary>
/// Works out what the library already holds for the units a search is about, in the profile's own
/// ranking terms — the baseline a candidate has to beat.
/// <para>
/// It is also where the two vocabularies for a resolution meet. Library labels what it measured from a
/// file's geometry (<c>1080p</c>); a release name parses into the closed enum (<c>R1080p</c>). Both
/// describe the same thing and both reach this class, so it accepts either rather than making one of
/// them travel in the other's clothes.
/// </para>
/// </summary>
public sealed class CurrentQualityResolver(ILibraryQuery library)
{
    /// <summary>
    /// The baseline for a search, or null when there is none — which is the ordinary case of acquiring
    /// something we do not have, and the case where the evaluator behaves exactly as it always did.
    /// <para>
    /// Two ways to get null, and they mean the same thing operationally. Nothing is on disk for one of
    /// the units, so this is not an upgrade at all but a gap being filled: whatever is acceptable is
    /// welcome. Or a unit's version has no readable quality, so there is no honest baseline to compare
    /// with, and inventing one would replace a good file on the strength of a guess.
    /// </para>
    /// </summary>
    /// <param name="unitIds">
    /// The catalog units the search is for. A season pack asks about several at once, and then the
    /// weakest one sets the bar: a pack that betters the worst episode is worth having even if it only
    /// matches the rest.
    /// </param>
    public async Task<CurrentRelease?> ResolveAsync(
        AcquisitionProfile profile,
        IReadOnlyList<Guid> unitIds,
        CancellationToken cancellationToken = default)
    {
        if (unitIds.Count == 0)
        {
            return null;
        }

        CurrentRelease? weakest = null;
        foreach (var unitId in unitIds)
        {
            var quality = await library.GetCurrentQualityAsync(unitId, cancellationToken);
            if (quality is null)
            {
                return null;
            }

            var current = Rank(profile, quality);
            if (weakest is null || current.Rank < weakest.Rank)
            {
                weakest = current;
            }
        }

        return weakest;
    }

    /// <summary>
    /// What the profile thinks this quality is worth. A quality the profile no longer allows ranks below
    /// everything it does allow, so any acceptable release betters it — which is the right reading of an
    /// owner who removed that quality from their allowed set.
    /// </summary>
    private static CurrentRelease Rank(AcquisitionProfile profile, ReleaseQuality quality)
    {
        var label = $"{quality.Source}/{quality.Resolution}";
        if (!TryParseSource(quality.Source, out var source) || !TryParseResolution(quality.Resolution, out var resolution))
        {
            return new CurrentRelease(int.MinValue, label);
        }

        var allowed = profile.AllowedQualities.FirstOrDefault(q => q.Source == source && q.Resolution == resolution);
        return new CurrentRelease(allowed?.Rank ?? int.MinValue, label);
    }

    private static bool TryParseSource(string? value, out QualitySource source) =>
        Enum.TryParse(value, ignoreCase: true, out source) && Enum.IsDefined(source);

    /// <summary>
    /// Accepts both spellings of a resolution: the enum's own name (<c>R1080p</c>) and the bare label
    /// Library derives from a video stream's height (<c>1080p</c>).
    /// </summary>
    private static bool TryParseResolution(string? value, out QualityResolution resolution)
    {
        resolution = default;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var name = char.IsAsciiDigit(value[0]) ? $"R{value}" : value;
        return Enum.TryParse(name, ignoreCase: true, out resolution) && Enum.IsDefined(resolution);
    }
}
