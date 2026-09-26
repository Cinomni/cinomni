using Cinomni.Acquisition.Contracts;
using Cinomni.Catalog.Contracts;
using Cinomni.Import.Contracts;
using Cinomni.Kernel.Messaging;
using Cinomni.Kernel.Security;
using Cinomni.Metadata.Contracts;
using Cinomni.Notifications.Contracts;
using Cinomni.Notifications.Messaging;
using Cinomni.Operations.Messaging;
using Cinomni.Operations.Persistence;
using Cinomni.Operations.Transactions;
using Cinomni.Requests.Contracts;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Cinomni.Notifications.Tests;

/// <summary>
/// Integration tests for the Notifications spine against a real PostgreSQL instance: a raised event flows
/// to an in-app notification (with the work title resolved from Catalog, →i), is idempotent under
/// redelivery, and fans out to enabled channels (best-effort). Events are driven by alternately draining
/// the outbox and the command queue.
/// </summary>
public sealed class NotificationFlowTests : IAsyncLifetime
{
    private static readonly Viewer Operator = new(Guid.NewGuid(), IsAdministrator: true);
    private static readonly Viewer Member = new(Guid.NewGuid(), IsAdministrator: false);

    private readonly FakeNotificationDispatcher _dispatcher = new();
    private ServiceProvider _host = null!;

    public async Task InitializeAsync() => _host = await NotificationsTestHost.CreateAsync("cinomni_test_notifications", _dispatcher);

    public async Task DisposeAsync() => await _host.DisposeAsync();

    [Fact]
    public async Task Raising_media_available_notifies_with_the_work_title()
    {
        var workId = await AddMovieAsync("The Matrix");

        await RaiseAsync(NotificationKind.MediaAvailable, workId);
        await DrainAsync();

        var notification = Assert.Single(await ListAsync(unreadOnly: false));
        Assert.Equal(NotificationSeverity.Success, notification.Severity);
        Assert.Equal("The Matrix is ready to watch", notification.Title);
        Assert.Equal(workId, notification.WorkId);
        Assert.False(notification.Read);
        Assert.Equal(1, await UnreadCountAsync());
    }

    [Fact]
    public async Task A_media_available_event_notifies_end_to_end()
    {
        var workId = await AddMovieAsync("The Matrix");

        await PublishAsync(new MediaAvailable(
            Guid.NewGuid(), workId, [], Guid.NewGuid(), Guid.NewGuid(), "/library/the-matrix.mkv", 1024, MediaInfo.Empty));
        await DrainAsync();

        var notification = Assert.Single(await ListAsync(unreadOnly: false));
        Assert.Equal(NotificationSeverity.Success, notification.Severity);
        Assert.Equal("The Matrix is ready to watch", notification.Title);
        Assert.Equal(workId, notification.WorkId);
    }

    [Fact]
    public async Task Marking_all_read_clears_every_unread()
    {
        await RaiseAsync(NotificationKind.MediaAvailable, await AddMovieAsync("The Matrix"));
        await RaiseAsync(NotificationKind.MediaAvailable, await AddMovieAsync("Dune"));
        await DrainAsync();
        Assert.Equal(2, await UnreadCountAsync());

        await MarkAllReadAsync();

        Assert.Equal(0, await UnreadCountAsync());
        Assert.Empty(await ListAsync(unreadOnly: true));
    }

    [Fact]
    public async Task Marking_one_read_leaves_the_others_unread()
    {
        await RaiseAsync(NotificationKind.MediaAvailable, await AddMovieAsync("The Matrix"));
        await RaiseAsync(NotificationKind.MediaAvailable, await AddMovieAsync("Dune"));
        await DrainAsync();

        var first = (await ListAsync(unreadOnly: true))[0];
        await MarkReadAsync(first.Id);

        Assert.Equal(1, await UnreadCountAsync());
        var stillUnread = Assert.Single(await ListAsync(unreadOnly: true));
        Assert.NotEqual(first.Id, stillUnread.Id);
    }

    [Fact]
    public async Task A_raised_notification_announces_itself_with_its_audience()
    {
        // The live stream cannot learn that an inbox changed from the events that *caused* the
        // notification: those fire before it exists, and several of them raise none at all.
        await PublishAsync(new ProviderDegraded(await AddMovieAsync("Dune"), "tmdb", 3, "provider-unavailable"));
        await DrainAsync();

        var notification = Assert.Single(await ListAsync(unreadOnly: false));
        var raised = Assert.Single(await RaisedAnnouncementsAsync());
        Assert.Equal(notification.Id, raised.NotificationId);
        // A degraded provider is an operator concern, and the announcement has to say so or the signal
        // would reach a household member's browser.
        Assert.True(raised.AdminOnly);
    }

    [Fact]
    public async Task A_household_notification_is_announced_to_the_household()
    {
        await PublishAsync(new MediaAvailable(
            Guid.NewGuid(), await AddMovieAsync("The Matrix"), [], Guid.NewGuid(), Guid.NewGuid(),
            "/library/the-matrix.mkv", 1024, MediaInfo.Empty));
        await DrainAsync();

        var raised = Assert.Single(await RaisedAnnouncementsAsync());
        Assert.False(raised.AdminOnly);
    }

    [Fact]
    public async Task A_redelivered_command_announces_nothing_a_second_time()
    {
        var dedupKey = $"test:{Guid.NewGuid()}";
        await RaiseAsync(NotificationKind.MediaAvailable, await AddMovieAsync("Dune"), dedupKey: dedupKey);
        await RaiseAsync(NotificationKind.MediaAvailable, null, dedupKey: dedupKey);
        await DrainAsync();

        Assert.Single(await ListAsync(unreadOnly: false));
        Assert.Single(await RaisedAnnouncementsAsync());
    }

    [Fact]
    public async Task An_acquisition_failed_event_raises_an_error_notification()
    {
        var workId = await AddMovieAsync("Dune");

        await PublishAsync(new AcquisitionFailed(Guid.NewGuid(), workId, "No release met the quality profile.", 3));
        await DrainAsync();

        var notification = Assert.Single(await ListAsync(unreadOnly: false));
        Assert.Equal(NotificationSeverity.Error, notification.Severity);
        Assert.Equal("Couldn't acquire Dune", notification.Title);
        Assert.Equal("No release met the quality profile.", notification.Body);
    }

    [Fact]
    public async Task A_provider_degraded_event_raises_a_warning_notification()
    {
        var workId = await AddMovieAsync("Dune");

        await PublishAsync(new ProviderDegraded(workId, "tmdb", 3, "provider-unavailable"));
        await DrainAsync();

        var notification = Assert.Single(await ListAsync(unreadOnly: false));
        Assert.Equal(NotificationSeverity.Warning, notification.Severity);
        Assert.Contains("tmdb", notification.Title);
    }

    [Fact]
    public async Task A_media_requested_event_surfaces_who_asked_for_what()
    {
        // The title is not catalogued yet, so the notification carries no work — only the copy.
        await PublishAsync(new MediaRequested(
            Guid.NewGuid(), "Dune", 2021, "tmdb", "438631", Guid.NewGuid(), "alice"));
        await DrainAsync();

        var notification = Assert.Single(await ListAsync(unreadOnly: false));
        Assert.Equal(NotificationSeverity.Info, notification.Severity);
        Assert.Equal("A title is waiting for approval", notification.Title);
        Assert.Equal("alice requested Dune (2021).", notification.Body);
        Assert.Null(notification.WorkId);
    }

    [Fact]
    public async Task Operator_notifications_never_reach_a_regular_account()
    {
        var workId = await AddMovieAsync("Dune");

        // One for everyone, two only an operator can act on (and that name what someone else is up to).
        await RaiseAsync(NotificationKind.MediaAvailable, workId);
        await PublishAsync(new MediaRequested(Guid.NewGuid(), "Dune", 2021, "tmdb", "438631", Guid.NewGuid(), "alice"));
        await PublishAsync(new ProviderDegraded(workId, "tmdb", 3, "provider-unavailable"));
        await DrainAsync();

        Assert.Equal(3, (await ListAsync(unreadOnly: false)).Count);
        Assert.Equal(3, await UnreadCountAsync());

        var forEveryone = Assert.Single(await ListAsync(unreadOnly: false, Member));
        Assert.Equal("media-available", forEveryone.Type);
        Assert.Equal(1, await UnreadCountAsync(Member));
    }

    [Fact]
    public async Task A_notification_about_a_title_the_reader_may_not_see_does_not_reach_them()
    {
        // Arrange — two titles ready to watch; one of them on a shelf the member holds no grant for.
        var hiddenWorkId = await AddMovieAsync("Grown-up Film");
        var openWorkId = await AddMovieAsync("Family Film");
        await using (var scope = _host.CreateAsyncScope())
        {
            var collections = scope.ServiceProvider.GetRequiredService<ICollectionAdministration>();
            var shelf = await collections.CreateAsync("Grown-ups", CollectionKind.Movies, CollectionAccessMode.Restricted);
            Assert.True((await collections.MoveWorkAsync(new WorkId(hiddenWorkId), shelf.Value)).IsSuccess);
        }

        await RaiseAsync(NotificationKind.MediaAvailable, hiddenWorkId);
        await RaiseAsync(NotificationKind.MediaAvailable, openWorkId);
        await DrainAsync();
        var hidden = Assert.Single(await ListAsync(unreadOnly: false), n => n.WorkId == hiddenWorkId);

        // Act — the member reads everything, and tries the hidden one by id.
        var seen = await ListAsync(unreadOnly: false, Member);
        var unreadBefore = await UnreadCountAsync(Member);
        await MarkReadAsync(hidden.Id, Member);
        await MarkAllReadAsync(Member);

        // Assert — neither its title nor its id reaches the member, and they cannot touch it.
        Assert.Equal(openWorkId, Assert.Single(seen).WorkId);
        Assert.Equal(1, unreadBefore);
        Assert.Equal(0, await UnreadCountAsync(Member));
        Assert.Equal(2, await UnreadCountAsync());
        await using var check = _host.CreateAsyncScope();
        var receipts = await check.ServiceProvider.GetRequiredService<Persistence.NotificationsDbContext>().Reads
            .CountAsync(r => r.UserId == Member.UserId && r.NotificationId == hidden.Id);
        Assert.Equal(0, receipts);
    }

    [Fact]
    public async Task A_regular_account_cannot_mark_an_operator_notification_read()
    {
        await PublishAsync(new MediaRequested(Guid.NewGuid(), "Dune", 2021, "tmdb", "438631", Guid.NewGuid(), "alice"));
        await DrainAsync();
        var operatorOnly = Assert.Single(await ListAsync(unreadOnly: false));

        // Neither by id nor by marking everything read: what they cannot see, they cannot touch.
        await MarkReadAsync(operatorOnly.Id, Member);
        await MarkAllReadAsync(Member);

        Assert.Equal(1, await UnreadCountAsync());
    }

    [Fact]
    public async Task Read_state_belongs_to_the_account_that_read_it()
    {
        await RaiseAsync(NotificationKind.MediaAvailable, await AddMovieAsync("The Matrix"));
        await DrainAsync();
        var notification = Assert.Single(await ListAsync(unreadOnly: false));

        await MarkReadAsync(notification.Id, Member);

        // The member has read it; the operator has not, and their badge says so.
        Assert.True(Assert.Single(await ListAsync(unreadOnly: false, Member)).Read);
        Assert.Equal(0, await UnreadCountAsync(Member));
        Assert.False(Assert.Single(await ListAsync(unreadOnly: false)).Read);
        Assert.Equal(1, await UnreadCountAsync());
    }

    [Fact]
    public async Task Marking_everything_read_clears_only_the_readers_own_badge()
    {
        await RaiseAsync(NotificationKind.MediaAvailable, await AddMovieAsync("The Matrix"));
        await RaiseAsync(NotificationKind.MediaAvailable, await AddMovieAsync("Dune"));
        await DrainAsync();

        await MarkAllReadAsync(Member);

        Assert.Equal(0, await UnreadCountAsync(Member));
        Assert.Equal(2, await UnreadCountAsync());
        Assert.Equal(2, (await ListAsync(unreadOnly: true)).Count);
    }

    [Fact]
    public async Task Marking_the_same_notification_read_twice_is_harmless()
    {
        await RaiseAsync(NotificationKind.MediaAvailable, await AddMovieAsync("The Matrix"));
        await DrainAsync();
        var notification = Assert.Single(await ListAsync(unreadOnly: false));

        await MarkReadAsync(notification.Id, Member);
        await MarkReadAsync(notification.Id, Member);
        await MarkAllReadAsync(Member);

        Assert.Equal(0, await UnreadCountAsync(Member));
        Assert.True(Assert.Single(await ListAsync(unreadOnly: false, Member)).Read);
    }

    [Fact]
    public async Task Marking_an_unknown_notification_read_is_a_no_op()
    {
        await RaiseAsync(NotificationKind.MediaAvailable, await AddMovieAsync("The Matrix"));
        await DrainAsync();

        await MarkReadAsync(Guid.NewGuid(), Member);

        Assert.Equal(1, await UnreadCountAsync(Member));
    }

    [Fact]
    public async Task A_command_with_a_seen_dedup_key_does_not_duplicate()
    {
        var workId = await AddMovieAsync("The Matrix");

        // Two commands for the same source event (same dedup key) — as a recovery/retry re-execution would
        // present — must land exactly one notification.
        await RaiseAsync(NotificationKind.MediaAvailable, workId, dedupKey: "media-available:same");
        await DrainAsync();
        await RaiseAsync(NotificationKind.MediaAvailable, workId, dedupKey: "media-available:same");
        await DrainAsync();

        Assert.Single(await ListAsync(unreadOnly: false));
        Assert.Equal(1, await UnreadCountAsync());
    }

    [Fact]
    public async Task Enabled_channels_receive_the_notification_disabled_ones_do_not()
    {
        var workId = await AddMovieAsync("The Matrix");
        var channels = _host.GetRequiredService<INotificationChannels>();
        await channels.AddAsync(NotificationChannelKind.Webhook, "Home webhook", "https://example.com/hook");
        var disabled = (await channels.AddAsync(NotificationChannelKind.Discord, "Muted", "https://discord.example/wh")).Value;
        await channels.SetEnabledAsync(disabled, false);

        await RaiseAsync(NotificationKind.MediaAvailable, workId);
        await DrainAsync();

        var delivered = Assert.Single(_dispatcher.Delivered);
        Assert.Equal("Home webhook", delivered.Channel.Name);
    }

    [Fact]
    public async Task A_failing_channel_does_not_stop_the_notification()
    {
        var workId = await AddMovieAsync("The Matrix");
        var channels = _host.GetRequiredService<INotificationChannels>();
        await channels.AddAsync(NotificationChannelKind.Webhook, "Broken", "https://example.com/hook");
        _dispatcher.Fail = true;

        await RaiseAsync(NotificationKind.MediaAvailable, workId);
        await DrainAsync();

        // The channel threw, but the in-app notification still landed and the command completed.
        Assert.Single(await ListAsync(unreadOnly: false));
        Assert.Equal(1, await UnreadCountAsync());
        Assert.Empty(_dispatcher.Delivered);
    }

    // -- helpers ---------------------------------------------------------------------------------

    private async Task<Guid> AddMovieAsync(string title)
    {
        await using var scope = _host.CreateAsyncScope();
        var commands = scope.ServiceProvider.GetRequiredService<ICatalogCommands>();
        var result = await commands.AddMovieAsync(title, year: null, []);
        return result.Value.Value;
    }

    private async Task RaiseAsync(NotificationKind kind, Guid? workId, string? detail = null, string? dedupKey = null)
    {
        await using var scope = _host.CreateAsyncScope();
        var queue = scope.ServiceProvider.GetRequiredService<ICommandQueue>();
        var command = new RaiseNotificationCommand(kind, workId, detail, dedupKey ?? $"test:{Guid.NewGuid()}");
        // A distinct queue key per call so each command enqueues (the handler, not the queue, decides no-op).
        await queue.EnqueueAsync(command, idempotencyKey: $"test-raise:{Guid.NewGuid()}");
    }

    private async Task PublishAsync(IDomainEvent domainEvent)
    {
        await using var scope = _host.CreateAsyncScope();
        var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
        var eventBus = scope.ServiceProvider.GetRequiredService<IEventBus>();
        await unitOfWork.ExecuteAsync(async token => await eventBus.PublishAsync(domainEvent, token));
    }

    /// <summary>Every <c>NotificationRaised</c> the module put in the outbox, in order.</summary>
    private async Task<List<NotificationRaised>> RaisedAnnouncementsAsync()
    {
        await using var scope = _host.CreateAsyncScope();
        var serializer = scope.ServiceProvider.GetRequiredService<IMessageSerializer>();
        var rows = await scope.ServiceProvider.GetRequiredService<OperationsDbContext>()
            .Outbox
            .AsNoTracking()
            .Where(m => m.EventType == NotificationEventNames.NotificationRaised)
            .OrderBy(m => m.OccurredAt)
            .ToListAsync();

        return rows.ConvertAll(row => (NotificationRaised)serializer.Deserialize(row.Payload, typeof(NotificationRaised)));
    }

    private async Task<IReadOnlyList<Notification>> ListAsync(bool unreadOnly, Viewer? reader = null)
    {
        await using var scope = _host.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<INotificationQuery>()
            .ListAsync(reader ?? Operator, unreadOnly, 50);
    }

    private async Task<int> UnreadCountAsync(Viewer? reader = null)
    {
        await using var scope = _host.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<INotificationQuery>().UnreadCountAsync(reader ?? Operator);
    }

    private async Task MarkReadAsync(Guid id, Viewer? reader = null)
    {
        await using var scope = _host.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<INotificationInbox>().MarkReadAsync(reader ?? Operator, id);
    }

    private async Task MarkAllReadAsync(Viewer? reader = null)
    {
        await using var scope = _host.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<INotificationInbox>().MarkAllReadAsync(reader ?? Operator);
    }

    private async Task DrainAsync()
    {
        while (true)
        {
            var commands = await DrainCommandsAsync();
            var events = await DrainOutboxAsync();
            if (commands == 0 && events == 0)
            {
                break;
            }
        }
    }

    private async Task<int> DrainCommandsAsync()
    {
        await using var scope = _host.CreateAsyncScope();
        var processor = scope.ServiceProvider.GetRequiredService<CommandProcessor>();
        var total = 0;
        int processed;
        while ((processed = await processor.ProcessBatchAsync()) > 0)
        {
            total += processed;
        }

        return total;
    }

    private async Task<int> DrainOutboxAsync()
    {
        await using var scope = _host.CreateAsyncScope();
        var relay = scope.ServiceProvider.GetRequiredService<OutboxRelay>();
        var total = 0;
        int published;
        while ((published = await relay.ProcessBatchAsync()) > 0)
        {
            total += published;
        }

        return total;
    }
}
