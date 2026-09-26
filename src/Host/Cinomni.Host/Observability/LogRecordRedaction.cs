using Cinomni.Kernel.Diagnostics;
using OpenTelemetry;
using OpenTelemetry.Logs;

namespace Cinomni.Host.Observability;

/// <summary>
/// Applies the telemetry vocabulary to log records, so an exported log line is held to exactly the same
/// rule as a span attribute: <b>a log record is as public as a metric label.</b>
/// <para>
/// This exists because the rendered text of this product's own log messages is full of the values
/// <see cref="CinomniTelemetry"/> forbids — a library root, a media path, an info hash, a search term —
/// and an exception's message and stack trace routinely quote the input that produced them (an FFmpeg
/// failure carries the media path and the whole argument list). Correlation between a log line and its
/// trace is worth exporting; the household's watch history and the operator's filesystem layout are not.
/// </para>
/// <para>
/// Three rules, applied to every record on its way to the exporter:
/// <list type="number">
/// <item>the body is the <b>message template</b> — <c>"Library root {Root} is not accessible."</c> —
/// never the interpolated text. An operator learns which code path fired, how often, and in which trace;
/// the values stay in the console, where the installation's own operator reads them;</item>
/// <item>attributes are filtered to the closed <see cref="CinomniTelemetry.Tags"/> vocabulary, so the
/// structured state a call site passed does not travel under a different name than the body;</item>
/// <item>an exception is reduced to its type. The type says what went wrong; the message and the stack
/// trace say what it was given.</item>
/// </list>
/// </para>
/// <para>
/// The rule is intentionally deny-by-default: a log message added anywhere in the product is safe to
/// export without its author having to think about this file, and a new value can only reach a collector
/// by being added to the vocabulary on purpose.
/// </para>
/// </summary>
internal sealed class LogRecordRedactionProcessor : BaseProcessor<LogRecord>
{
    /// <summary>
    /// The state key the BCL puts the message template under. It is kept: it is the one attribute whose
    /// value is a compile-time constant written by us rather than data from a running installation.
    /// </summary>
    internal const string OriginalFormatKey = "{OriginalFormat}";

    /// <summary>What is left of an exception. A CLR type name is bounded and carries no input.</summary>
    internal const string ExceptionTypeKey = "exception.type";

    /// <summary>
    /// The tag keys a log record may carry out of the process. The same closed list as a span attribute,
    /// minus <see cref="CinomniTelemetry.Tags.EntityId"/>: an identifier is allowed on a span, which is
    /// already scoped to one unit of work, and is not needed to read an aggregated log stream.
    /// </summary>
    private static readonly HashSet<string> AllowedKeys = new(StringComparer.Ordinal)
    {
        CinomniTelemetry.Tags.Module,
        CinomniTelemetry.Tags.EventName,
        CinomniTelemetry.Tags.CommandName,
        CinomniTelemetry.Tags.JobName,
        CinomniTelemetry.Tags.IndexerName,
        CinomniTelemetry.Tags.Method,
        CinomniTelemetry.Tags.Outcome,
        CinomniTelemetry.Tags.Reason,
        CinomniTelemetry.Tags.State,
        CinomniTelemetry.Tags.Direction,
    };

    /// <summary>
    /// Runs after the record is complete and before the exporter's batch processor sees it, which is why
    /// this is registered ahead of the exporter.
    /// </summary>
    public override void OnEnd(LogRecord data)
    {
        var template = Template(data.Attributes);

        // Attributes first: setting them also replaces the raw state, so nothing downstream can read the
        // original values through the legacy path either.
        data.Attributes = Filter(data.Attributes, data.Exception, template);

        // The template, or nothing at all. A record with no template is one whose only text is already
        // rendered — there is no safe half of it to publish, so the body goes rather than the values.
        data.Body = template;
        data.FormattedMessage = null;
        data.Exception = null;
    }

    private static string? Template(IReadOnlyList<KeyValuePair<string, object?>>? attributes)
    {
        if (attributes is null)
        {
            return null;
        }

        foreach (var attribute in attributes)
        {
            if (string.Equals(attribute.Key, OriginalFormatKey, StringComparison.Ordinal))
            {
                return attribute.Value as string;
            }
        }

        return null;
    }

    private static List<KeyValuePair<string, object?>> Filter(
        IReadOnlyList<KeyValuePair<string, object?>>? attributes,
        Exception? exception,
        string? template)
    {
        var kept = new List<KeyValuePair<string, object?>>(capacity: 2);

        if (template is not null)
        {
            kept.Add(new KeyValuePair<string, object?>(OriginalFormatKey, template));
        }

        if (attributes is not null)
        {
            foreach (var attribute in attributes)
            {
                if (AllowedKeys.Contains(attribute.Key))
                {
                    kept.Add(attribute);
                }
            }
        }

        if (exception is not null)
        {
            kept.Add(new KeyValuePair<string, object?>(ExceptionTypeKey, exception.GetType().FullName));
        }

        return kept;
    }
}
