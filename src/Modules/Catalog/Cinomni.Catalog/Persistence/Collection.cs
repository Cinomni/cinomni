using Cinomni.Catalog.Application;
using Cinomni.Catalog.Contracts;
using Cinomni.Kernel.Identifiers;

namespace Cinomni.Catalog.Persistence;

/// <summary>
/// A shelf of works: what a household browses, and the unit access is granted on. Catalog owns it
/// because Catalog owns the works that sit in it — placement, collection and grant in one schema is
/// what makes "may this account see this work" a single query.
/// </summary>
public sealed class Collection
{
    private const int NameMax = 200;

    public Guid Id { get; init; }

    public required string Name { get; set; }

    public CollectionKind Kind { get; init; }

    public CollectionAccessMode AccessMode { get; set; }

    /// <summary>Where a work lands when nobody says otherwise. Exactly one collection carries this.</summary>
    public bool IsDefault { get; init; }

    public DateTimeOffset CreatedAt { get; init; }

    public static Collection Create(string name, CollectionKind kind, CollectionAccessMode accessMode, DateTimeOffset now) => new()
    {
        Id = Uuid7.New(),
        Name = Text.Truncate(name.Trim(), NameMax)!,
        Kind = kind,
        AccessMode = accessMode,
        IsDefault = false,
        CreatedAt = now,
    };

    public CollectionSummary ToSummary(int workCount) =>
        new(new CollectionId(Id), Name, Kind, AccessMode, IsDefault, workCount);
}

/// <summary>
/// The collection every installation starts with, and where a work lands when no collection is named.
/// Its id is a compile-time constant rather than a minted UUIDv7 because the migration has to insert
/// the row and backfill existing works against it in SQL — the value must be identical in both places.
/// It is the one id in the product that is not generated.
/// </summary>
public static class DefaultCollection
{
    public static readonly Guid Id = new("01000000-0000-7000-8000-000000000001");

    public const string Name = "Library";

    /// <summary>
    /// Open on purpose: an upgrade must leave every existing account seeing exactly what it saw before,
    /// and Catalog cannot enumerate accounts to write them grants (they belong to Identity).
    /// </summary>
    public static Collection Create(DateTimeOffset now) => new()
    {
        Id = Id,
        Name = Name,
        Kind = CollectionKind.Mixed,
        AccessMode = CollectionAccessMode.Open,
        IsDefault = true,
        CreatedAt = now,
    };
}
