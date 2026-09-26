using Cinomni.Kernel.Identifiers;
using Cinomni.Kernel.Messaging;
using Cinomni.Notifications.Contracts;
using Cinomni.Notifications.Messaging;
using Cinomni.Notifications.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Cinomni.Notifications.Tests;

/// <summary>
/// Integration tests for the inbox retention sweep against a real PostgreSQL instance: an old
/// notification leaves with its read receipts, a recent one stays whether or not it was read, and the
/// configured delivery channels are never touched.
/// </summary>
public sealed class NotificationRetentionTests : IAsyncLifetime
{
    private static readonly TimeSpan Window = TimeSpan.FromDays(180);

    private readonly FakeNotificationDispatcher _dispatcher = new();
    private ServiceProvider _provider = null!;

    public async Task InitializeAsync() =>
        _provider = await NotificationsTestHost.CreateAsync("cinomni_test_notifications_retention", _dispatcher);

    public async Task DisposeAsync() => await _provider.DisposeAsync();

    [Fact]
    public async Task An_old_notification_leaves_with_its_read_receipts()
    {
        var reader = Uuid7.New();
        var ancient = await SeedAsync("ancient", DateTimeOffset.UtcNow - Window - TimeSpan.FromDays(2), reader);
        var recent = await SeedAsync("recent", DateTimeOffset.UtcNow - TimeSpan.FromDays(2), reader);

        await PurgeAsync();

        await using var scope = _provider.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<NotificationsDbContext>();

        Assert.False(await dbContext.Notifications.AnyAsync(n => n.Id == ancient));
        Assert.False(await dbContext.Reads.AnyAsync(r => r.NotificationId == ancient));

        Assert.True(await dbContext.Notifications.AnyAsync(n => n.Id == recent));
        Assert.True(await dbContext.Reads.AnyAsync(r => r.NotificationId == recent));
    }

    [Fact]
    public async Task Channels_survive_and_a_second_run_removes_nothing()
    {
        await using (var scope = _provider.CreateAsyncScope())
        {
            var channels = scope.ServiceProvider.GetRequiredService<INotificationChannels>();
            Assert.True(
                (await channels.AddAsync(NotificationChannelKind.Webhook, "Ops", "https://hooks.example/ops"))
                .IsSuccess);
        }

        await SeedAsync("ancient", DateTimeOffset.UtcNow - Window - TimeSpan.FromDays(5), reader: null);

        await PurgeAsync();
        await PurgeAsync();

        await using (var scope = _provider.CreateAsyncScope())
        {
            var dbContext = scope.ServiceProvider.GetRequiredService<NotificationsDbContext>();
            Assert.Equal(0, await dbContext.Notifications.CountAsync());
            Assert.Equal(1, await dbContext.Channels.CountAsync());
        }
    }

    private async Task PurgeAsync()
    {
        await using var scope = _provider.CreateAsyncScope();
        var handler = scope.ServiceProvider.GetRequiredService<ICommandHandler<PurgeNotificationsCommand>>();
        Assert.True((await handler.HandleAsync(new PurgeNotificationsCommand())).IsSuccess);
    }

    private async Task<Guid> SeedAsync(string dedupKey, DateTimeOffset createdAt, Guid? reader)
    {
        await using var scope = _provider.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<NotificationsDbContext>();

        var notification = NotificationRecord.Create(
            dedupKey,
            "test.notification",
            NotificationSeverity.Info,
            "A title",
            "A body",
            workId: null,
            adminOnly: false,
            createdAt);

        dbContext.Notifications.Add(notification);

        if (reader is not null)
        {
            dbContext.Reads.Add(new NotificationReadRecord
            {
                NotificationId = notification.Id,
                UserId = reader.Value,
                ReadAt = createdAt,
            });
        }

        await dbContext.SaveChangesAsync();
        return notification.Id;
    }
}
