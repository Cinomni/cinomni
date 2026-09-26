using System.Text.Json;

namespace Cinomni.Import.Persistence;

/// <summary>
/// Serialises the acquisition's unit scope to/from the job's <c>jsonb</c> column — the same embedded
/// value pattern as <see cref="MediaInfoSerializer"/>. A plain array of identifiers rather than a
/// child table: the scope is read as a whole or not at all, it is never joined on, and it belongs to
/// the hand-off that opened the job rather than to anything the job discovers.
/// </summary>
internal static class UnitScopeSerializer
{
    /// <summary>
    /// The scope as json, or null when there is nothing to record. An empty collection maps to null
    /// on purpose: "the acquisition supplied no scope" and "the acquisition asked for no units" are
    /// the same fact here, and the resolver reads an absent scope as unconstrained either way.
    /// </summary>
    public static string? ToJson(IReadOnlyList<Guid>? unitIds) =>
        unitIds is { Count: > 0 } ? JsonSerializer.Serialize(unitIds) : null;

    public static IReadOnlyList<Guid>? FromJson(string? json)
    {
        if (string.IsNullOrEmpty(json))
        {
            return null;
        }

        var unitIds = JsonSerializer.Deserialize<List<Guid>>(json);
        return unitIds is { Count: > 0 } ? unitIds : null;
    }
}
