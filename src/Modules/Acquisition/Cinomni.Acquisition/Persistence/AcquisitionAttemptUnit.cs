namespace Cinomni.Acquisition.Persistence;

/// <summary>
/// One catalog unit an attempt claims to be acquiring — a work id for a movie, an episode id for
/// each episode a season pack covers. Append-only alongside the attempt it belongs to.
/// <para>
/// It exists so the attempt trail reads truthfully: without it, a pack downloaded once for a season
/// goal leaves no record of which episodes it was meant to satisfy, and the ten sibling goals it
/// closed look as though they were met by nothing at all.
/// </para>
/// </summary>
public sealed class AcquisitionAttemptUnit
{
    public Guid AttemptId { get; init; }

    public Guid UnitId { get; init; }
}
