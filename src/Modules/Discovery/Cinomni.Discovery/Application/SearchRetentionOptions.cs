namespace Cinomni.Discovery.Application;

/// <summary>
/// How long the federated search history is kept. It is pure working memory: Decision reads an
/// execution's results within seconds of the search, and everything worth remembering afterwards has
/// already been copied into an evaluation, an acquisition attempt or a download task.
/// </summary>
public sealed class SearchRetentionOptions
{
    /// <summary>
    /// Minimum age of a search before its month may be dropped. <b>This is a floor, not a ceiling.</b>
    /// Retention drops whole monthly partitions, so a month disappears only once its <i>last</i>
    /// instant has aged past this window: with the default 30 days, a search run on the first of a
    /// month is kept for roughly 60 days, and one run on the last day for roughly 30. The effective
    /// ceiling is this window plus the remainder of the row's own month plus one job interval.
    /// <para>
    /// The error is deliberately in the safe direction — nothing is ever deleted before the window —
    /// and it is the price of an O(1) drop. Configure it as "no search is kept less than this".
    /// </para>
    /// </summary>
    public TimeSpan SearchRetention { get; set; } = TimeSpan.FromDays(30);

    /// <summary>How often <c>discovery.retention</c> runs partition maintenance.</summary>
    public TimeSpan Interval { get; set; } = TimeSpan.FromDays(1);

    /// <exception cref="InvalidOperationException">The configured values are unusable.</exception>
    public void Validate()
    {
        if (SearchRetention <= TimeSpan.Zero)
        {
            throw new InvalidOperationException(
                $"Retention:Discovery:{nameof(SearchRetention)} must be a positive duration "
                + $"(configured: {SearchRetention}).");
        }

        if (Interval <= TimeSpan.Zero)
        {
            throw new InvalidOperationException(
                $"Retention:Discovery:{nameof(Interval)} must be a positive duration (configured: {Interval}).");
        }
    }
}
