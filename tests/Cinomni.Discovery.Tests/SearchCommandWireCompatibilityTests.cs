using Cinomni.Discovery.Messaging;
using Cinomni.Operations.Messaging;
using Cinomni.Search.Contracts;

namespace Cinomni.Discovery.Tests;

/// <summary>
/// Wire-compatibility guard for Discovery's command payload. <c>ExecuteSearchCommand</c> is persisted
/// as jsonb in <c>operations.command</c> and dispatched from the stored row, so a deploy always finds
/// in-flight rows written by the previous version. Nothing in the build catches a reordered, renamed
/// or newly-required member.
/// <para>
/// It lives here rather than in the platform suite because the command is declared in Discovery's
/// <em>implementation</em> assembly, and <c>Cinomni.Operations.Tests</c> is deliberately
/// contracts-only.
/// </para>
/// </summary>
public sealed class SearchCommandWireCompatibilityTests
{
    private const string TargetId = "0198b0a1-0000-7000-8000-000000000002";
    private const string WorkId = "0198b0a1-0000-7000-8000-000000000001";

    private readonly MessageSerializer _serializer = new();

    [Fact]
    public void A_pre_series_execute_search_row_still_deserializes()
    {
        var payload = $$"""
                        {
                          "criterion": {
                            "term": "Interstellar",
                            "year": 2014,
                            "imdbId": "tt0816692",
                            "tmdbId": "157336",
                            "contentKind": "Movie"
                          },
                          "targetId": "{{TargetId}}"
                        }
                        """;

        var restored = (ExecuteSearchCommand)_serializer.Deserialize(payload, typeof(ExecuteSearchCommand));

        // The old members must still land on their properties, so a rename cannot pass vacuously...
        Assert.Equal(Guid.Parse(TargetId), restored.TargetId);
        Assert.Equal("Interstellar", restored.Criterion.Term);
        Assert.Equal(2014, restored.Criterion.Year);
        Assert.Equal("Movie", restored.Criterion.ContentKind);

        // ...and every new member must arrive at its default.
        Assert.Null(restored.WorkId);
        Assert.Null(restored.UnitIds);
        Assert.Null(restored.Criterion.SeasonNumber);
        Assert.Null(restored.Criterion.TvdbId);
    }

    [Fact]
    public void The_new_members_survive_a_round_trip()
    {
        // The mirror image: the correlation must actually reach the wire, or it would be silently
        // dropped between the enqueue and the dispatch.
        var unit = Guid.Parse(WorkId);
        var command = new ExecuteSearchCommand(
            new SearchCriterion("The Wire", 2002, null, null, "Episode", SeasonNumber: 2, EpisodeNumber: 5, TvdbId: "79126"),
            Guid.Parse(TargetId),
            unit,
            [unit]);

        var restored = (ExecuteSearchCommand)_serializer.Deserialize(
            _serializer.Serialize(command), typeof(ExecuteSearchCommand));

        Assert.Equal(unit, restored.WorkId);
        Assert.Equal(unit, Assert.Single(restored.UnitIds!));
        Assert.Equal(2, restored.Criterion.SeasonNumber);
        Assert.Equal(5, restored.Criterion.EpisodeNumber);
        Assert.Equal("79126", restored.Criterion.TvdbId);
    }
}
