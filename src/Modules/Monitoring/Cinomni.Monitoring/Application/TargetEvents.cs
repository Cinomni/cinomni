using Cinomni.Monitoring.Contracts;
using Cinomni.Monitoring.Persistence;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Cinomni.Monitoring.Application;

/// <summary>Shared construction of the module's announcements and the constraint it converges on.</summary>
internal static class TargetEvents
{
    /// <summary>
    /// Builds the enable announcement, carrying the target's kind and the catalog unit it watches. For a
    /// movie the unit is the work id, so the payload stays movie-identical to the 1.x shape plus the two
    /// additive members.
    /// </summary>
    public static MonitoringEnabled Enabled(MonitoredTarget target) =>
        new(target.Id, target.WorkId, target.Mode.ToString(), target.Kind.ToString(), target.TargetRef);

    /// <summary>
    /// True when the failure is a duplicate <c>(work_id, kind, target_ref)</c> — a concurrent apply or
    /// sync of the same work. Catching it is what lets the loser converge on the winner's rows instead of
    /// surfacing a 500 or minting a second root.
    /// </summary>
    public static bool IsDuplicateTarget(DbUpdateException ex) =>
        ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation };
}
