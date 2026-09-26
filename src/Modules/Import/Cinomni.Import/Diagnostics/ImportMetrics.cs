using System.Diagnostics;
using System.Diagnostics.Metrics;
using Cinomni.Kernel.Diagnostics;

namespace Cinomni.Import.Diagnostics;

/// <summary>
/// How long an import takes and how it ended. Import is the longest synchronous piece of an
/// acquisition — it scans a directory, fingerprints every file, hardlinks each one into the library
/// and probes the result with ffprobe — so it is also the one whose slowness an operator notices as
/// "the download finished ages ago and it is still not in the library".
/// <para>
/// <b>No path ever becomes a tag.</b> A staging path and a library path are private filesystem layout,
/// and a library path additionally spells out the title. The outcome word is the whole label set.
/// </para>
/// </summary>
public static class ImportMetrics
{
    /// <summary>Outcome tag values, one per terminal state the job can reach.</summary>
    public static class Outcomes
    {
        /// <summary>Files landed and were announced as assets.</summary>
        public const string Registered = "registered";

        /// <summary>Nothing in the download matched — it waits for a manual import.</summary>
        public const string Unmatched = "unmatched";

        /// <summary>Matched, but nothing could be landed.</summary>
        public const string Rejected = "rejected";

        /// <summary>Left recoverable: the library root was unreachable, or a redelivery was ignored.</summary>
        public const string Deferred = "deferred";
    }

    private static readonly Meter Meter = new(CinomniTelemetry.MeterName);

    private static readonly KeyValuePair<string, object?> ModuleTag =
        new(CinomniTelemetry.Tags.Module, CinomniTelemetry.Modules.Import);

    private static readonly Histogram<double> Duration = Meter.CreateHistogram<double>(
        "cinomni.import.duration",
        unit: "s",
        description: "Time one completed download took to be scanned, landed and probed.");

    private static readonly Counter<long> Jobs = Meter.CreateCounter<long>(
        "cinomni.import.jobs",
        unit: "{job}",
        description: "Import jobs by outcome.");

    /// <summary>Records one import job.</summary>
    /// <param name="outcome">One of <see cref="Outcomes"/>.</param>
    /// <param name="duration">Wall time for the whole job, including ffprobe.</param>
    public static void RecordJob(string outcome, TimeSpan duration)
    {
        var tags = new TagList
        {
            ModuleTag,
            new KeyValuePair<string, object?>(CinomniTelemetry.Tags.Outcome, outcome),
        };

        Jobs.Add(1, tags);
        Duration.Record(duration.TotalSeconds, tags);
    }
}
