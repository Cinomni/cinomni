namespace Cinomni.Catalog.Persistence;

/// <summary>
/// One account's access to one restricted collection. Absence means no access, so the table only holds
/// what was deliberately given. The account is a bare id: Catalog does not own accounts and must not
/// reference Identity's tables, which also means a grant for an account that no longer exists is inert
/// rather than broken — no session can present that id.
/// </summary>
public sealed class CollectionGrant
{
    public Guid CollectionId { get; init; }

    public Guid UserId { get; init; }

    public DateTimeOffset GrantedAt { get; init; }

    public Guid GrantedByUserId { get; init; }

    public static CollectionGrant Create(Guid collectionId, Guid userId, Guid grantedByUserId, DateTimeOffset now) => new()
    {
        CollectionId = collectionId,
        UserId = userId,
        GrantedAt = now,
        GrantedByUserId = grantedByUserId,
    };
}
