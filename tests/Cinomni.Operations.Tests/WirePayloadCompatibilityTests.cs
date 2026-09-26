using System.Globalization;
using Cinomni.Acquisition.Contracts;
using Cinomni.Decision.Contracts;
using Cinomni.Downloads.Contracts;
using Cinomni.Import.Contracts;
using Cinomni.Kernel.Messaging;
using Cinomni.Library.Contracts;
using Cinomni.Monitoring.Contracts;
using Cinomni.Operations.Messaging;
using Cinomni.Search.Contracts;

namespace Cinomni.Operations.Tests;

/// <summary>
/// Wire-compatibility guard for the acquisition spine. Every payload below is persisted as jsonb in
/// <c>operations.outbox</c> / <c>operations.command</c> and is dispatched from the stored row, so a
/// deploy always finds in-flight rows written by the previous version. Nothing in the build catches a
/// reordered, renamed or newly-required member — this suite does.
/// <para>
/// Each literal is the exact JSON a pre-series (1.x) row holds, written by hand rather than produced
/// by the current code, and it goes through the real <see cref="MessageSerializer"/> the outbox uses.
/// Both halves are asserted: the old members must still land on their properties (so a renamed member
/// cannot pass vacuously by defaulting), and every new member must arrive at its default.
/// </para>
/// </summary>
public sealed class WirePayloadCompatibilityTests
{
    private const string EventId = "0198b0a1-0000-7000-8000-00000000e1d0";
    private const string OccurredAt = "2026-07-28T10:15:00+00:00";
    private const string WorkId = "0198b0a1-0000-7000-8000-000000000001";
    private const string TargetId = "0198b0a1-0000-7000-8000-000000000002";
    private const string IntentId = "0198b0a1-0000-7000-8000-000000000003";
    private const string AttemptId = "0198b0a1-0000-7000-8000-000000000004";
    private const string AssetId = "0198b0a1-0000-7000-8000-000000000005";
    private const string ImportJobId = "0198b0a1-0000-7000-8000-000000000006";
    private const string DownloadTaskId = "0198b0a1-0000-7000-8000-000000000007";
    private const string EvaluationId = "0198b0a1-0000-7000-8000-000000000008";
    private const string VersionId = "0198b0a1-0000-7000-8000-000000000009";

    private readonly MessageSerializer _serializer = new();

    [Fact]
    public void Widened_event_payloads_deserialize_from_a_pre_series_row()
    {
        AssertSearchRequested();
        AssertMonitoringEnabled();
        AssertReleaseSelected();
        AssertDownloadQueued();
        AssertDownloadCompleted();
        AssertImportCompleted();
        AssertMediaAvailable();
        AssertMediaAssetRegistered();
    }

    [Fact]
    public void The_new_members_survive_a_round_trip_through_the_outbox_serializer()
    {
        // The mirror image of the guard above: the series members must actually reach the wire, or
        // the correlation would be silently dropped between the outbox write and the dispatch.
        var criterion = new SearchCriterion(
            "The Wire", 2002, "tt0306414", "1438", "Episode",
            SeasonNumber: 2, EpisodeNumber: 5, AbsoluteNumber: 18,
            AirDate: new DateOnly(2003, 7, 13), TvdbId: "79126");
        var unit = Guid.Parse(WorkId);

        var search = RoundTrip(new SearchRequested(
            Guid.Parse(TargetId), unit, criterion, "Missing", "2026072810", UnitIds: [unit]));
        Assert.Equal(2, search.Criterion.SeasonNumber);
        Assert.Equal(new DateOnly(2003, 7, 13), search.Criterion.AirDate);
        Assert.Equal("79126", search.Criterion.TvdbId);
        Assert.Equal(unit, Assert.Single(search.UnitIds!));

        var enabled = RoundTrip(new MonitoringEnabled(
            Guid.Parse(TargetId), unit, "All", Kind: "Episode", UnitId: unit));
        Assert.Equal("Episode", enabled.Kind);
        Assert.Equal(unit, enabled.UnitId);

        var completed = RoundTrip(new DownloadCompleted(
            Guid.Parse(DownloadTaskId), Guid.Parse(IntentId), Guid.Parse(AttemptId), unit, Guid.Parse(TargetId),
            "abc", "/data/staging/Pack", UnitIds: [unit], Files: [new DownloadedFile(0, "Pack/first.mkv", 1000)]));
        Assert.Equal(unit, Assert.Single(completed.UnitIds!));
        var file = Assert.Single(completed.Files!);
        Assert.Equal("Pack/first.mkv", file.RelativePath);
        Assert.Equal(1000, file.Size);

        var media = RoundTrip(new MediaAvailable(
            Guid.Parse(AssetId), unit, [Guid.Parse(TargetId)], Guid.Parse(ImportJobId), Guid.Parse(DownloadTaskId),
            "/data/library/x.mkv", 10, MediaInfo.Empty, UnitIds: [unit], SeasonNumber: 2, EpisodeNumbers: [5, 6]));
        Assert.Equal(2, media.SeasonNumber);
        Assert.Equal([5, 6], media.EpisodeNumbers!);

        var registered = RoundTrip(new MediaAssetRegistered(
            Guid.Parse(AssetId), unit, Guid.Parse(VersionId), 2, UnitIds: [unit]));
        Assert.Equal(unit, Assert.Single(registered.UnitIds!));
    }

    // -- per-payload assertions ------------------------------------------------------------------

    private void AssertSearchRequested()
    {
        var payload = $$"""
                        {
                          "targetId": "{{TargetId}}",
                          "workId": "{{WorkId}}",
                          "criterion": {
                            "term": "The Wire",
                            "year": 2002,
                            "imdbId": "tt0306414",
                            "tmdbId": "1438",
                            "contentKind": "Movie"
                          },
                          "reason": "Missing",
                          "window": "2026072810",
                          "eventId": "{{EventId}}",
                          "occurredAt": "{{OccurredAt}}",
                          "idempotencyKey": "search-requested:{{TargetId}}:2026072810"
                        }
                        """;

        var restored = Deserialize<SearchRequested>(payload);

        Assert.Equal(Guid.Parse(TargetId), restored.TargetId);
        Assert.Equal("The Wire", restored.Criterion.Term);
        Assert.Equal("Movie", restored.Criterion.ContentKind);
        Assert.Equal("2026072810", restored.Window);
        Assert.Null(restored.UnitIds);
        Assert.Null(restored.Criterion.SeasonNumber);
        Assert.Null(restored.Criterion.EpisodeNumber);
        Assert.Null(restored.Criterion.AbsoluteNumber);
        Assert.Null(restored.Criterion.AirDate);
        Assert.Null(restored.Criterion.TvdbId);
    }

    private void AssertMonitoringEnabled()
    {
        var payload = $$"""
                        {
                          "targetId": "{{TargetId}}",
                          "workId": "{{WorkId}}",
                          "mode": "All",
                          "eventId": "{{EventId}}",
                          "occurredAt": "{{OccurredAt}}",
                          "idempotencyKey": "monitoring-enabled:{{TargetId}}:638000000000000000"
                        }
                        """;

        var restored = Deserialize<MonitoringEnabled>(payload);

        Assert.Equal(Guid.Parse(TargetId), restored.TargetId);
        Assert.Equal("All", restored.Mode);
        Assert.Null(restored.Kind);
        Assert.Null(restored.UnitId);
    }

    private void AssertReleaseSelected()
    {
        var payload = $$"""
                        {
                          "evaluationId": "{{EvaluationId}}",
                          "targetId": "{{TargetId}}",
                          "releaseGuid": "guid-1",
                          "downloadUrl": "magnet:?xt=urn:btih:abc",
                          "eventId": "{{EventId}}",
                          "occurredAt": "{{OccurredAt}}",
                          "idempotencyKey": "release-selected:{{EvaluationId}}"
                        }
                        """;

        var restored = Deserialize<ReleaseSelected>(payload);

        Assert.Equal(Guid.Parse(EvaluationId), restored.EvaluationId);
        Assert.Equal("guid-1", restored.ReleaseGuid);
        Assert.Equal("magnet:?xt=urn:btih:abc", restored.DownloadUrl);
        Assert.Null(restored.UnitIds);
    }

    private void AssertDownloadQueued()
    {
        var payload = $$"""
                        {
                          "attemptId": "{{AttemptId}}",
                          "intentId": "{{IntentId}}",
                          "workId": "{{WorkId}}",
                          "targetId": "{{TargetId}}",
                          "releaseGuid": "guid-1",
                          "downloadUrl": "magnet:?xt=urn:btih:abc",
                          "savePath": null,
                          "eventId": "{{EventId}}",
                          "occurredAt": "{{OccurredAt}}",
                          "idempotencyKey": "download-queued:{{AttemptId}}"
                        }
                        """;

        var restored = Deserialize<DownloadQueued>(payload);

        Assert.Equal(Guid.Parse(AttemptId), restored.AttemptId);
        Assert.Equal(Guid.Parse(WorkId), restored.WorkId);
        Assert.Null(restored.SavePath);
        Assert.Null(restored.UnitIds);
    }

    private void AssertDownloadCompleted()
    {
        var payload = $$"""
                        {
                          "downloadTaskId": "{{DownloadTaskId}}",
                          "intentId": "{{IntentId}}",
                          "attemptId": "{{AttemptId}}",
                          "workId": "{{WorkId}}",
                          "targetId": "{{TargetId}}",
                          "infoHash": "0123456789abcdef",
                          "contentPath": "/data/staging/Movie.2024",
                          "eventId": "{{EventId}}",
                          "occurredAt": "{{OccurredAt}}",
                          "idempotencyKey": "download-completed:{{DownloadTaskId}}"
                        }
                        """;

        var restored = Deserialize<DownloadCompleted>(payload);

        Assert.Equal(Guid.Parse(DownloadTaskId), restored.DownloadTaskId);
        Assert.Equal("0123456789abcdef", restored.InfoHash);
        Assert.Equal("/data/staging/Movie.2024", restored.ContentPath);
        Assert.Null(restored.UnitIds);
        Assert.Null(restored.Files);
    }

    private void AssertImportCompleted()
    {
        var payload = $$"""
                        {
                          "importJobId": "{{ImportJobId}}",
                          "intentId": "{{IntentId}}",
                          "attemptId": "{{AttemptId}}",
                          "assetId": "{{AssetId}}",
                          "eventId": "{{EventId}}",
                          "occurredAt": "{{OccurredAt}}",
                          "idempotencyKey": "import-completed:{{ImportJobId}}"
                        }
                        """;

        var restored = Deserialize<ImportCompleted>(payload);

        Assert.Equal(Guid.Parse(ImportJobId), restored.ImportJobId);
        Assert.Equal(Guid.Parse(AssetId), restored.AssetId);
        Assert.Null(restored.UnitIds);
    }

    private void AssertMediaAvailable()
    {
        // MediaStreamKind travels as its numeric value: the outbox serializer is plain
        // JsonSerializerDefaults.Web with no JsonStringEnumConverter.
        var payload = $$"""
                        {
                          "assetId": "{{AssetId}}",
                          "workId": "{{WorkId}}",
                          "targetIds": [ "{{TargetId}}" ],
                          "importJobId": "{{ImportJobId}}",
                          "downloadTaskId": "{{DownloadTaskId}}",
                          "fullPath": "/data/library/Movie/Movie.mkv",
                          "size": 2000000000,
                          "mediaInfo": {
                            "container": "matroska,webm",
                            "durationSeconds": 6000,
                            "bitrate": 8000000,
                            "streams": [
                              {
                                "index": 0,
                                "kind": 1,
                                "codec": "h264",
                                "language": null,
                                "width": 1920,
                                "height": 1080,
                                "channels": null,
                                "isDefault": true,
                                "isForced": false
                              }
                            ]
                          },
                          "eventId": "{{EventId}}",
                          "occurredAt": "{{OccurredAt}}",
                          "idempotencyKey": "media-available:{{AssetId}}"
                        }
                        """;

        var restored = Deserialize<MediaAvailable>(payload);

        Assert.Equal(Guid.Parse(AssetId), restored.AssetId);
        Assert.Equal(Guid.Parse(WorkId), restored.WorkId);
        Assert.Equal(Guid.Parse(TargetId), Assert.Single(restored.TargetIds));
        Assert.Equal(2_000_000_000, restored.Size);
        Assert.Equal("matroska,webm", restored.MediaInfo.Container);
        var stream = Assert.Single(restored.MediaInfo.Streams);
        Assert.Equal(MediaStreamKind.Video, stream.Kind);
        Assert.Equal("h264", stream.Codec);
        Assert.Null(restored.UnitIds);
        Assert.Null(restored.SeasonNumber);
        Assert.Null(restored.EpisodeNumbers);
    }

    private void AssertMediaAssetRegistered()
    {
        var payload = $$"""
                        {
                          "assetId": "{{AssetId}}",
                          "workId": "{{WorkId}}",
                          "versionId": "{{VersionId}}",
                          "streamCount": 2,
                          "eventId": "{{EventId}}",
                          "occurredAt": "{{OccurredAt}}",
                          "idempotencyKey": "media-asset-registered:{{AssetId}}"
                        }
                        """;

        var restored = Deserialize<MediaAssetRegistered>(payload);

        Assert.Equal(Guid.Parse(AssetId), restored.AssetId);
        Assert.Equal(Guid.Parse(WorkId), restored.WorkId);
        Assert.Equal(Guid.Parse(VersionId), restored.VersionId);
        Assert.Equal(2, restored.StreamCount);
        Assert.Null(restored.UnitIds);
    }

    // -- helpers ---------------------------------------------------------------------------------

    /// <summary>Reads a stored payload exactly as the outbox relay's dispatcher does.</summary>
    private T Deserialize<T>(string payload)
        where T : IDomainEvent
    {
        var restored = (T)_serializer.Deserialize(payload, typeof(T));

        // The stored row also carries the envelope the base record owns; losing it would break
        // ordering and deduplication just as badly as losing a payload member.
        Assert.Equal(Guid.Parse(EventId), restored.EventId);
        Assert.Equal(DateTimeOffset.Parse(OccurredAt, CultureInfo.InvariantCulture), restored.OccurredAt);
        return restored;
    }

    private T RoundTrip<T>(T message)
        where T : IDomainEvent =>
        (T)_serializer.Deserialize(_serializer.Serialize(message), typeof(T));
}
