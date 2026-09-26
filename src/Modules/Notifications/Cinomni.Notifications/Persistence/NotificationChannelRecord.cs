using Cinomni.Kernel.Identifiers;
using Cinomni.Notifications.Contracts;

namespace Cinomni.Notifications.Persistence;

/// <summary>A configured outbound delivery channel (Notifications module). The target is the webhook URL.</summary>
public sealed class NotificationChannelRecord
{
    private const int NameMax = 200;
    private const int TargetMax = 2000;

    public Guid Id { get; init; }

    public NotificationChannelKind Kind { get; init; }

    public required string Name { get; init; }

    public required string Target { get; init; }

    public bool Enabled { get; set; }

    public DateTimeOffset CreatedAt { get; init; }

    public static NotificationChannelRecord Create(
        NotificationChannelKind kind,
        string name,
        string target,
        DateTimeOffset now) => new()
    {
        Id = Uuid7.New(),
        Kind = kind,
        Name = Text.Truncate(name, NameMax),
        Target = Text.Truncate(target, TargetMax),
        Enabled = true,
        CreatedAt = now,
    };

    public NotificationChannel ToContract() => new(Id, Kind, Name, Target, Enabled);
}
