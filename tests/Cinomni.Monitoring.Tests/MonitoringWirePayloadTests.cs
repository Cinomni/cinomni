using Cinomni.Monitoring.Contracts;
using Cinomni.Monitoring.Messaging;
using Cinomni.Operations.Messaging;

namespace Cinomni.Monitoring.Tests;

/// <summary>
/// Wire guard for the module's own persisted command payloads. They live in
/// <c>operations.command</c> as jsonb and are dispatched from the stored row, so a deploy always finds
/// rows written by the previous version. Nothing in the build catches a reordered or reinterpreted member.
/// <para>
/// The sharp edge here is <see cref="MonitoringMode"/>: the outbox serializer is plain
/// <c>JsonSerializerDefaults.Web</c> with no <c>JsonStringEnumConverter</c>, so the enum travels as its
/// <em>numeric</em> value even though the database column stores it as text. Renumbering
/// <c>None</c>/<c>All</c> to make room for the hierarchical modes would have silently reinterpreted every
/// queued policy command; they were appended above instead.
/// </para>
/// </summary>
public sealed class MonitoringWirePayloadTests
{
    private const string WorkId = "0198b0a1-0000-7000-8000-000000000001";

    private readonly MessageSerializer _serializer = new();

    [Fact]
    public void A_pre_series_apply_policy_row_still_means_monitor_everything()
    {
        var payload = $$"""
                        {
                          "workId": "{{WorkId}}",
                          "mode": 1
                        }
                        """;

        var restored = (ApplyMonitoringPolicyCommand)_serializer.Deserialize(
            payload, typeof(ApplyMonitoringPolicyCommand));

        Assert.Equal(Guid.Parse(WorkId), restored.WorkId);
        Assert.Equal(MonitoringMode.All, restored.Mode);

        // A row from before InitialOnly existed was always an unconditional apply.
        Assert.False(restored.InitialOnly);

        // ...and the disabled row keeps meaning disabled.
        var none = (ApplyMonitoringPolicyCommand)_serializer.Deserialize(
            $$"""{ "workId": "{{WorkId}}", "mode": 0 }""", typeof(ApplyMonitoringPolicyCommand));
        Assert.Equal(MonitoringMode.None, none.Mode);
    }

    [Fact]
    public void The_hierarchical_modes_survive_a_round_trip()
    {
        foreach (var mode in Enum.GetValues<MonitoringMode>())
        {
            var restored = RoundTrip(new ApplyMonitoringPolicyCommand(Guid.Parse(WorkId), mode));
            Assert.Equal(mode, restored.Mode);
        }
    }

    [Fact]
    public void The_new_series_commands_survive_a_round_trip()
    {
        var snapshotId = Guid.NewGuid();
        var sync = RoundTrip(new SyncSeriesTargetsCommand(Guid.Parse(WorkId), snapshotId));
        Assert.Equal(snapshotId, sync.SnapshotId);

        var unit = Guid.NewGuid();
        var satisfied = RoundTrip(new MarkUnitsSatisfiedCommand(Guid.Parse(WorkId), Guid.NewGuid(), [unit]));
        Assert.Equal(unit, Assert.Single(satisfied.UnitIds));
    }

    [Fact]
    public void A_work_added_row_from_before_the_monitored_flag_still_means_watch_it()
    {
        // WorkAdded rows already in the outbox carry no "monitored"; each was a work someone asked for.
        var payload = $$"""{ "workId": "{{WorkId}}", "kind": "Movie", "title": "The Matrix", "year": 1999 }""";

        var restored = (Catalog.Contracts.WorkAdded)_serializer.Deserialize(payload, typeof(Catalog.Contracts.WorkAdded));

        Assert.True(restored.Monitored);
        Assert.False(RoundTripEvent(restored with { Monitored = false }).Monitored);
    }

    private Catalog.Contracts.WorkAdded RoundTripEvent(Catalog.Contracts.WorkAdded domainEvent) =>
        (Catalog.Contracts.WorkAdded)_serializer.Deserialize(_serializer.Serialize(domainEvent), typeof(Catalog.Contracts.WorkAdded));

    private T RoundTrip<T>(T command)
        where T : Kernel.Messaging.ICommand =>
        (T)_serializer.Deserialize(_serializer.Serialize(command), typeof(T));
}
