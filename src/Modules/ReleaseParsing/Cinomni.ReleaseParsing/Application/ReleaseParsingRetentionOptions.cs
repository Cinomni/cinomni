namespace Cinomni.ReleaseParsing.Application;

/// <summary>
/// How long the parse audit trail is kept. Long by default, and safe to shorten: the record is
/// derived deterministically from the release title and the command that writes it is a
/// get-or-create, so a title parsed again after its row was purged simply recreates it and
/// re-publishes <c>ReleaseParsed</c>. Nothing downstream reads the row by primary key.
/// </summary>
public sealed class ReleaseParsingRetentionOptions
{
    /// <summary>Minimum age of a parse record before it may be removed.</summary>
    public TimeSpan ParseRetention { get; set; } = TimeSpan.FromDays(180);

    /// <summary>How often <c>parsing.retention</c> runs.</summary>
    public TimeSpan Interval { get; set; } = TimeSpan.FromDays(1);

    /// <exception cref="InvalidOperationException">The configured values are unusable.</exception>
    public void Validate()
    {
        if (ParseRetention <= TimeSpan.Zero)
        {
            throw new InvalidOperationException(
                $"Retention:ReleaseParsing:{nameof(ParseRetention)} must be a positive duration "
                + $"(configured: {ParseRetention}).");
        }

        if (Interval <= TimeSpan.Zero)
        {
            throw new InvalidOperationException(
                $"Retention:ReleaseParsing:{nameof(Interval)} must be a positive duration "
                + $"(configured: {Interval}).");
        }
    }
}
