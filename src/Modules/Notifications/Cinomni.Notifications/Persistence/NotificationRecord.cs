using Cinomni.Kernel.Identifiers;
using Cinomni.Notifications.Contracts;

namespace Cinomni.Notifications.Persistence;

/// <summary>
/// A stored in-app notification (Notifications module). References a work by id (inter-schema, no
/// physical FK) — Notifications does not own the catalog. Projected to the public <c>Notification</c>
/// contract on read; whether it counts as read depends on who is asking, so that lives in
/// <see cref="NotificationReadRecord"/> rather than here.
/// </summary>
public sealed class NotificationRecord
{
    private const int TypeMax = 100;
    private const int TitleMax = 300;
    private const int BodyMax = 2000;
    private const int DedupKeyMax = 300;

    public Guid Id { get; init; }

    /// <summary>Stable business key of the source event (unique) — makes the raise idempotent under retry/recovery.</summary>
    public required string DedupKey { get; init; }

    public required string Type { get; init; }

    public NotificationSeverity Severity { get; init; }

    public required string Title { get; init; }

    public required string Body { get; init; }

    public Guid? WorkId { get; init; }

    /// <summary>Operator concern: only administrators ever see it in the inbox.</summary>
    public bool AdminOnly { get; init; }

    public DateTimeOffset CreatedAt { get; init; }

    public static NotificationRecord Create(
        string dedupKey,
        string type,
        NotificationSeverity severity,
        string title,
        string body,
        Guid? workId,
        bool adminOnly,
        DateTimeOffset now) => new()
    {
        Id = Uuid7.New(),
        DedupKey = Text.Truncate(dedupKey, DedupKeyMax),
        Type = Text.Truncate(type, TypeMax),
        Severity = severity,
        Title = Text.Truncate(title, TitleMax),
        Body = Text.Truncate(body, BodyMax),
        WorkId = workId,
        AdminOnly = adminOnly,
        CreatedAt = now,
    };

    public Notification ToContract(bool read) =>
        new(Id, Type, Severity, Title, Body, WorkId, read, CreatedAt);
}
