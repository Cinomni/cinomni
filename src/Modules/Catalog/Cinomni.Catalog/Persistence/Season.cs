namespace Cinomni.Catalog.Persistence;

/// <summary>
/// A season of a series: the middle level of the <c>Work (1) → Season (N) → Episode (M)</c> hierarchy,
/// and a catalog unit in its own right (a season pack is acquired against it).
/// <para>
/// Its natural key is <c>(WorkId, Number)</c> — that is what a structure sync upserts on, so a provider
/// that renumbers or reorders its seasons can never orphan the asset links, acquisition intents and
/// monitored targets pointing at <see cref="Id"/>. Season 0 is the specials season.
/// </para>
/// </summary>
public sealed class Season
{
    /// <summary>Maximum persisted length of <see cref="Title"/>.</summary>
    public const int TitleMaxLength = 500;

    /// <summary>Maximum persisted length of <see cref="PosterUrl"/>.</summary>
    public const int UrlMaxLength = 1000;

    public Guid Id { get; init; }

    /// <summary>The series this season belongs to (cascade parent).</summary>
    public Guid WorkId { get; init; }

    /// <summary>The season number as numbered by the claiming structure provider. 0 means specials.</summary>
    public int Number { get; init; }

    public string? Title { get; set; }

    /// <summary>The season premiere date, as published by the provider (date only — see <see cref="Episode.AirDate"/>).</summary>
    public DateOnly? AirDate { get; set; }

    /// <summary>How many episodes the provider says this season has, which can exceed the rows known so far.</summary>
    public int? ExpectedEpisodeCount { get; set; }

    public string? PosterUrl { get; set; }

    /// <summary>The metadata snapshot this season's descriptive fields came from, if any.</summary>
    public Guid? MetadataSnapshotId { get; private set; }

    public DateTimeOffset AddedAt { get; init; }

    public List<Episode> Episodes { get; } = [];

    /// <summary>
    /// Copies a provider snapshot's descriptive fields onto this season (the ACL), following
    /// <c>Work.ApplyMetadata</c>'s rule: a field is overwritten only when the snapshot supplies a value,
    /// so a sparser later provider never blanks what a richer earlier one filled in. The natural key
    /// (<see cref="WorkId"/>, <see cref="Number"/>) is never touched.
    /// </summary>
    public void ApplyMetadata(
        string? title,
        DateOnly? airDate,
        int? expectedEpisodeCount,
        string? posterUrl,
        Guid snapshotId)
    {
        if (title is not null)
        {
            Title = Text.Truncate(title, TitleMaxLength);
        }

        if (airDate is not null)
        {
            AirDate = airDate;
        }

        if (expectedEpisodeCount is not null)
        {
            ExpectedEpisodeCount = expectedEpisodeCount;
        }

        if (posterUrl is not null)
        {
            PosterUrl = Text.Truncate(posterUrl, UrlMaxLength);
        }

        MetadataSnapshotId = snapshotId;
    }
}
