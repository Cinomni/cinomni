using Cinomni.Acquisition.Messaging;
using Cinomni.Operations.Messaging;

namespace Cinomni.Acquisition.Tests;

/// <summary>
/// Wire-compatibility guard for Acquisition's command payloads. Both are persisted as jsonb in
/// <c>operations.command</c> and dispatched from the stored row, so a deploy always finds in-flight
/// rows written by the previous version. Nothing in the build catches a reordered, renamed or
/// newly-required member.
/// <para>
/// It lives here rather than in the platform suite because these commands are declared in
/// Acquisition's <em>implementation</em> assembly, and <c>Cinomni.Operations.Tests</c> is
/// deliberately contracts-only.
/// </para>
/// </summary>
public sealed class AcquisitionCommandWireCompatibilityTests
{
    private const string TargetId = "0198b0a1-0000-7000-8000-000000000002";
    private const string WorkId = "0198b0a1-0000-7000-8000-000000000001";
    private const string EvaluationId = "0198b0a1-0000-7000-8000-000000000008";

    private readonly MessageSerializer _serializer = new();

    [Fact]
    public void A_pre_series_create_intent_row_still_deserializes()
    {
        var payload = $$"""
                        {
                          "targetId": "{{TargetId}}",
                          "workId": "{{WorkId}}",
                          "mode": "All"
                        }
                        """;

        var restored = (CreateAcquisitionIntentCommand)_serializer.Deserialize(
            payload, typeof(CreateAcquisitionIntentCommand));

        Assert.Equal(Guid.Parse(TargetId), restored.TargetId);
        Assert.Equal(Guid.Parse(WorkId), restored.WorkId);
        Assert.Equal("All", restored.Mode);
        Assert.Null(restored.UnitId);
    }

    [Fact]
    public void A_pre_series_select_candidate_row_still_deserializes()
    {
        var payload = $$"""
                        {
                          "evaluationId": "{{EvaluationId}}",
                          "targetId": "{{TargetId}}",
                          "releaseGuid": "guid-1",
                          "downloadUrl": "magnet:?xt=urn:btih:abc"
                        }
                        """;

        var restored = (SelectCandidateCommand)_serializer.Deserialize(payload, typeof(SelectCandidateCommand));

        Assert.Equal(Guid.Parse(EvaluationId), restored.EvaluationId);
        Assert.Equal("guid-1", restored.ReleaseGuid);
        Assert.Equal("magnet:?xt=urn:btih:abc", restored.DownloadUrl);
        Assert.Null(restored.UnitIds);
    }

    [Fact]
    public void The_new_members_survive_a_round_trip()
    {
        var unit = Guid.Parse(WorkId);

        var created = (CreateAcquisitionIntentCommand)_serializer.Deserialize(
            _serializer.Serialize(new CreateAcquisitionIntentCommand(Guid.Parse(TargetId), unit, "All", unit)),
            typeof(CreateAcquisitionIntentCommand));
        Assert.Equal(unit, created.UnitId);

        var satisfied = (MarkUnitsSatisfiedCommand)_serializer.Deserialize(
            _serializer.Serialize(new MarkUnitsSatisfiedCommand([unit], Guid.Parse(EvaluationId))),
            typeof(MarkUnitsSatisfiedCommand));
        Assert.Equal(unit, Assert.Single(satisfied.UnitIds));
        Assert.Equal(Guid.Parse(EvaluationId), satisfied.AssetId);
    }
}
