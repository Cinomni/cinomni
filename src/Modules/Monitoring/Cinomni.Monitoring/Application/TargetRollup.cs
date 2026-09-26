using Cinomni.Monitoring.Contracts;
using Cinomni.Monitoring.Persistence;

namespace Cinomni.Monitoring.Application;

/// <summary>
/// Rolls the leaves' missing state up the target tree: a season is missing while any of its episodes
/// is, and the series root is missing while any of its seasons is. Shared by the materialiser and the
/// import-satisfaction handler so "when does a season stop being missing?" has exactly one answer.
/// <para>
/// A node with no children keeps whatever it already had — a season whose episodes have not been
/// materialised yet must not silently report itself complete.
/// </para>
/// </summary>
internal static class TargetRollup
{
    /// <summary>Recomputes the season and series-root missing flags of one work's targets, in place.</summary>
    public static void Recompute(IReadOnlyList<MonitoredTarget> targets)
    {
        var childrenByParent = targets
            .Where(t => t.ParentTargetId is not null)
            .GroupBy(t => t.ParentTargetId!.Value)
            .ToDictionary(group => group.Key, group => group.ToList());

        // Seasons first: the root reads the seasons this pass just settled.
        Fold(targets, TargetKind.Season, childrenByParent);
        Fold(targets, TargetKind.Series, childrenByParent);
    }

    private static void Fold(
        IReadOnlyList<MonitoredTarget> targets,
        TargetKind kind,
        Dictionary<Guid, List<MonitoredTarget>> childrenByParent)
    {
        foreach (var parent in targets.Where(t => t.Kind == kind))
        {
            if (childrenByParent.TryGetValue(parent.Id, out var children) && children.Count > 0)
            {
                parent.IsMissing = children.Exists(child => child.IsMissing);
            }
        }
    }
}
