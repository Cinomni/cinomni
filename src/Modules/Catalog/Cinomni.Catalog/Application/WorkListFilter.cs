using Cinomni.Catalog.Contracts;
using Cinomni.Catalog.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Cinomni.Catalog.Application;

/// <summary>
/// Translates a <see cref="WorkListQuery"/> into SQL over an already-scoped <c>works</c> query. Every
/// filter narrows; nothing here can widen what <see cref="ContentAccess.Visible"/> let through.
/// </summary>
internal static class WorkListFilter
{
    /// <summary>A title search longer than any title is not a search; it is cut here rather than refused.</summary>
    public const int MaxTermLength = 200;

    public static IQueryable<Work> Apply(IQueryable<Work> works, WorkListQuery query)
    {
        if (query.Collection is { } shelf)
        {
            works = works.Where(w => w.CollectionId == shelf.Value);
        }

        if (query.Kind is { } kind)
        {
            works = works.Where(w => w.Kind == kind);
        }

        if (Pattern(query.Term) is { } pattern)
        {
            works = works.Where(w => EF.Functions.ILike(w.Title, pattern, @"\"));
        }

        if (!string.IsNullOrEmpty(query.Genre))
        {
            works = works.Where(w => w.Genres.Contains(query.Genre));
        }

        return query.Availability switch
        {
            // The same rule the rollups are rendered with: a movie, or a series with no episodes counted
            // yet, stands by its asset flag; a counted series by how many of its episodes have a file.
            WorkAvailability.Complete => works.Where(w =>
                ((w.Kind != WorkKind.Series || w.EpisodeCount == 0) && w.HasAsset)
                || (w.Kind == WorkKind.Series && w.EpisodeCount > 0
                    && w.AvailableEpisodeCount > 0 && w.AvailableEpisodeCount >= w.EpisodeCount)),
            WorkAvailability.Partial => works.Where(w =>
                w.Kind == WorkKind.Series && w.EpisodeCount > 0
                && w.AvailableEpisodeCount > 0 && w.AvailableEpisodeCount < w.EpisodeCount),
            WorkAvailability.None => works.Where(w =>
                ((w.Kind != WorkKind.Series || w.EpisodeCount == 0) && !w.HasAsset)
                || (w.Kind == WorkKind.Series && w.EpisodeCount > 0 && w.AvailableEpisodeCount <= 0)),
            _ => works,
        };
    }

    /// <summary>Every order ends on the id, so equal keys never let a work sit on two pages or on none.</summary>
    public static IOrderedQueryable<Work> Order(IQueryable<Work> works, WorkSort sort) => sort switch
    {
        WorkSort.Year => works
            .OrderBy(w => w.Year == null)
            .ThenByDescending(w => w.Year)
            .ThenBy(w => w.SortTitle)
            .ThenBy(w => w.Id),
        WorkSort.Added => works.OrderByDescending(w => w.AddedAt).ThenByDescending(w => w.Id),
        _ => works.OrderBy(w => w.SortTitle).ThenBy(w => w.Id),
    };

    /// <summary>
    /// A contains-pattern for <c>ILIKE</c> with the caller's text taken literally: <c>%</c>, <c>_</c> and the
    /// escape character itself are escaped, so "100%" finds "100%" and not everything. Null for no search.
    /// </summary>
    internal static string? Pattern(string? term)
    {
        var trimmed = term?.Trim();
        if (string.IsNullOrEmpty(trimmed))
        {
            return null;
        }

        if (trimmed.Length > MaxTermLength)
        {
            trimmed = trimmed[..MaxTermLength];
        }

        var escaped = trimmed.Replace(@"\", @"\\").Replace("%", @"\%").Replace("_", @"\_");
        return $"%{escaped}%";
    }
}
