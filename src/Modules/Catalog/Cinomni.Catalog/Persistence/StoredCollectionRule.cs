using System.Text.Json;
using System.Text.Json.Serialization;
using Cinomni.Catalog.Contracts;
using Cinomni.Kernel.Identifiers;

namespace Cinomni.Catalog.Persistence;

/// <summary>
/// One rule a collection carries. The conditions are one jsonb document rather than child rows: a rule
/// is only ever read and replaced whole, nothing points into a condition, and the validator — not the
/// schema — is what decides which conditions are legal.
/// </summary>
public sealed class StoredCollectionRule
{
    public const int NameMax = 200;

    /// <summary>Enums travel by name, as they do over HTTP, so a stored rule reads the same as a submitted one.</summary>
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };

    public Guid Id { get; init; }

    public Guid CollectionId { get; init; }

    public required string Name { get; set; }

    /// <summary>Order within the collection: lower is asked first. Collections themselves are ordered by <see cref="Collection.RulePriority"/>.</summary>
    public int Position { get; set; }

    public required string ConditionsJson { get; set; }

    public DateTimeOffset CreatedAt { get; init; }

    public DateTimeOffset UpdatedAt { get; set; }

    public static StoredCollectionRule Create(
        Guid collectionId,
        string name,
        int position,
        IReadOnlyList<CollectionRuleCondition> conditions,
        DateTimeOffset now) => new()
    {
        Id = Uuid7.New(),
        CollectionId = collectionId,
        Name = Text.Truncate(name.Trim(), NameMax)!,
        Position = position,
        ConditionsJson = Serialize(conditions),
        CreatedAt = now,
        UpdatedAt = now,
    };

    public void Update(string name, int position, IReadOnlyList<CollectionRuleCondition> conditions, DateTimeOffset now)
    {
        Name = Text.Truncate(name.Trim(), NameMax)!;
        Position = position;
        ConditionsJson = Serialize(conditions);
        UpdatedAt = now;
    }

    /// <summary>
    /// The stored conditions. A document that no longer reads — which only a hand edit could produce —
    /// yields none, and the matcher never matches an empty set, so a damaged rule claims nothing
    /// rather than stopping every placement in the library.
    /// </summary>
    public IReadOnlyList<CollectionRuleCondition> Conditions()
    {
        try
        {
            return JsonSerializer.Deserialize<List<CollectionRuleCondition>>(ConditionsJson, Json) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    public CollectionRule ToContract() => new(Id, new CollectionId(CollectionId), Name, Conditions());

    private static string Serialize(IReadOnlyList<CollectionRuleCondition> conditions) =>
        JsonSerializer.Serialize(conditions, Json);
}
