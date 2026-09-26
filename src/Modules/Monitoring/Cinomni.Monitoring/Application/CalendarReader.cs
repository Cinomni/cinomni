using Cinomni.Catalog.Contracts;
using Cinomni.Kernel.Security;
using Cinomni.Monitoring.Contracts;

namespace Cinomni.Monitoring.Application;

/// <summary>One airing the calendar shows. The work title comes from Catalog; Monitoring does not store it.</summary>
public sealed record CalendarEntry(
    Guid TargetId,
    Guid WorkId,
    string WorkTitle,
    TargetKind Kind,
    int? SeasonNumber,
    int? EpisodeNumber,
    string? EpisodeTitle,
    DateOnly AirDate,
    DateTimeOffset? AirsAt,
    bool Monitored,
    bool IsMissing);

/// <summary>
/// The calendar as one person sees it. Monitoring owns the dates; Catalog owns which works that person
/// may see and what those works are called. A hidden work is absent, not marked — the same rule as the
/// wanted list.
/// </summary>
public sealed class CalendarReader(IMonitoringQuery query, IContentAccess access, ICatalogQuery catalog)
{
    /// <summary>
    /// How many raw pages a member's calendar reads to fill one page of what they may see. Bounded, so a
    /// window full of hidden titles costs a few reads rather than the whole table.
    /// </summary>
    private const int MaxPagesPerRead = 5;

    public async Task<IReadOnlyList<CalendarEntry>> ListAsync(
        Viewer viewer,
        DateOnly from,
        DateOnly to,
        CancellationToken cancellationToken = default)
    {
        var airing = await VisibleAiringAsync(viewer, from, to, cancellationToken);
        var titles = await TitlesAsync(airing, cancellationToken);
        return airing.Select(target => ToEntry(target, titles)).ToList();
    }

    /// <summary>
    /// Up to a page of airings the viewer may see. Filtered page by page rather than after one capped
    /// read: cutting at the page size first and filtering second showed a member fewer rows than there
    /// were, with nothing to say so — the client only warns when the page comes back full.
    /// </summary>
    private async Task<List<MonitoredTargetSummary>> VisibleAiringAsync(
        Viewer viewer, DateOnly from, DateOnly to, CancellationToken cancellationToken)
    {
        const int pageSize = MonitoringPaging.MaxPageSize;
        var result = new List<MonitoredTargetSummary>();
        for (var page = 0; page < MaxPagesPerRead && result.Count < pageSize; page++)
        {
            var raw = await query.ListAiringAsync(from, to, pageSize, page * pageSize, cancellationToken);
            if (viewer.IsAdministrator)
            {
                return [.. raw];
            }

            if (raw.Count > 0)
            {
                var visible = await access.FilterWorksAsync(
                    viewer, raw.Select(t => t.WorkId.Value).Distinct().ToList(), cancellationToken);
                result.AddRange(raw.Where(t => visible.Contains(t.WorkId.Value)));
            }

            if (raw.Count < pageSize)
            {
                break;
            }
        }

        return result.Count > pageSize ? result[..pageSize] : result;
    }

    /// <summary>Every title on the page in one read of Catalog, not one per work.</summary>
    private async Task<Dictionary<Guid, string>> TitlesAsync(
        IReadOnlyList<MonitoredTargetSummary> airing,
        CancellationToken cancellationToken)
    {
        var ids = airing.Select(t => t.WorkId.Value).Distinct().ToList();
        var works = await catalog.GetByIdsAsync(ids, cancellationToken);
        return works.ToDictionary(work => work.Id.Value, work => work.Title);
    }

    private static CalendarEntry ToEntry(MonitoredTargetSummary target, Dictionary<Guid, string> titles)
    {
        var published = target.PublishedAirDate
            ?? (target.AirDate is { } airsAt ? DateOnly.FromDateTime(airsAt.UtcDateTime) : DateOnly.MinValue);
        titles.TryGetValue(target.WorkId.Value, out var title);
        return new CalendarEntry(
            target.Id.Value,
            target.WorkId.Value,
            string.IsNullOrWhiteSpace(title) ? target.Title ?? "Untitled" : title,
            target.Kind,
            target.SeasonNumber,
            target.EpisodeNumber,
            target.Title,
            published,
            target.AirDate,
            target.Monitored,
            target.IsMissing);
    }
}
