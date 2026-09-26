using Cinomni.Catalog.Contracts;
using Cinomni.Kernel.Messaging;
using Cinomni.Metadata.Contracts;
using Cinomni.Kernel.Results;
using Cinomni.Kernel.Security;
using Cinomni.Monitoring.Contracts;
using Cinomni.Operations.Messaging;
using Cinomni.Operations.Transactions;
using Cinomni.Requests.Contracts;
using Cinomni.Requests.Messaging;
using Cinomni.Requests.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace Cinomni.Requests.Tests;

/// <summary>
/// Integration tests for the request spine against a real PostgreSQL instance: a submitted request waits
/// for a decision, an approval catalogues the title through Catalog's public contract, and the work
/// becoming available closes the request. Events are driven by alternately draining the outbox and the
/// command queue, so the asynchronous fulfilment is exercised exactly as the Host runs it.
/// </summary>
public sealed class RequestFlowTests : IAsyncLifetime
{
    private static readonly Guid Alice = Guid.NewGuid();
    private static readonly Guid Operator = Guid.NewGuid();

    private readonly FakeMetadataRefresh _metadata = new();
    private readonly RequestEventSink _events = new();
    private readonly FakeMonitoring _monitoring = new();
    private ServiceProvider _host = null!;

    public async Task InitializeAsync() =>
        _host = await RequestsTestHost.CreateAsync(
            "cinomni_test_requests", _metadata, _events, monitoringFake: _monitoring);

    public async Task DisposeAsync() => await _host.DisposeAsync();

    [Fact]
    public async Task A_submitted_request_waits_for_a_decision()
    {
        var requestId = await SubmitAsync("The Matrix", "603");
        await DrainAsync();

        var request = Assert.Single(await ListAsync());
        Assert.Equal(requestId, request.Id);
        Assert.Equal(MediaRequestStatus.Pending, request.Status);
        Assert.Equal("alice", request.RequestedByUsername);
        Assert.Null(request.WorkId);
        Assert.Equal(1, await PendingCountAsync());

        // Nothing reaches the catalog until an operator says so.
        Assert.Null(await FindWorkAsync("603"));
    }

    [Fact]
    public async Task Approving_a_request_catalogues_the_title_and_enriches_it()
    {
        var requestId = await SubmitAsync("The Matrix", "603", year: 1999);

        Assert.True((await ApproveAsync(requestId)).IsSuccess);
        await DrainAsync();

        var work = await FindWorkAsync("603");
        Assert.NotNull(work);
        Assert.Equal("The Matrix", work.Title);
        Assert.Equal(1999, work.Year);

        var request = Assert.Single(await ListAsync());
        Assert.Equal(MediaRequestStatus.Approved, request.Status);
        Assert.Equal(work.Id.Value, request.WorkId);
        Assert.Equal(0, await PendingCountAsync());

        // The title was handed to the metadata ACL so artwork and details fill in.
        var refreshed = Assert.Single(_metadata.Refreshed);
        Assert.Equal(work.Id.Value, refreshed.WorkId);
        Assert.Equal("tmdb", refreshed.Provider);
        Assert.Equal("603", refreshed.ExternalId);

        // Watched straight away rather than when WorkAdded's default lands; that default then finds the
        // root already there and leaves it be.
        Assert.Equal((work.Id.Value, MonitoringMode.All), Assert.Single(_monitoring.Applied));
    }

    [Fact]
    public async Task Approving_a_series_request_catalogues_a_series_and_enriches_it_as_one()
    {
        // A request carries what kind of title it is. Without it every request was catalogued as a
        // movie and its metadata fetched from the movie endpoint under the show's id — an unrelated film
        // or nothing — and monitoring then searched for the show as a film.
        var requestId = await SubmitKindAsync("Game of Thrones", "1399", MediaRequestKind.Series);

        Assert.True((await ApproveAsync(requestId)).IsSuccess);
        await DrainAsync();

        var work = await FindWorkAsync("1399");
        Assert.NotNull(work);
        Assert.Equal(WorkKind.Series, work.Kind);
        Assert.Equal(MetadataMediaKind.Series, Assert.Single(_metadata.Refreshed).Kind);
        Assert.Equal(MediaRequestKind.Series, Assert.Single(await ListAsync()).Kind);
    }

    [Fact]
    public async Task A_movie_and_a_show_that_share_a_provider_id_are_different_requests()
    {
        // TMDB numbers films and shows independently, so the same id names two unrelated titles.
        Assert.True((await SubmitKindResultAsync("Some Film", "1399", MediaRequestKind.Movie)).IsSuccess);
        Assert.True((await SubmitKindResultAsync("Some Show", "1399", MediaRequestKind.Series)).IsSuccess);
        Assert.Equal(2, (await ListAsync()).Count);

        // And each kind still refuses a second live request of its own.
        var again = await SubmitKindResultAsync("Some Show", "1399", MediaRequestKind.Series);
        Assert.Equal("requests.duplicate", again.Error.Code);
    }

    [Fact]
    public async Task A_show_whose_id_a_film_in_the_library_already_uses_gets_a_work_of_its_own()
    {
        // TMDB numbers films and shows independently. A film already holding the number must neither
        // make the show "already in the library" nor be handed back as the show's work.
        var filmId = await AddMovieAsync("Some Film", "1399");
        var requestId = await SubmitKindAsync("Some Show", "1399", MediaRequestKind.Series);

        Assert.True((await ApproveAsync(requestId)).IsSuccess);
        await DrainAsync();

        var request = Assert.Single(await ListAsync());
        Assert.NotNull(request.WorkId);
        Assert.NotEqual(filmId, request.WorkId);
        Assert.Equal(MetadataMediaKind.Series, Assert.Single(_metadata.Refreshed).Kind);
    }

    [Fact]
    public async Task A_trusted_requester_skips_the_queue()
    {
        // Identity grants auto-approval; the request lands decided and fulfilment starts from the same
        // transaction an operator's approval would have produced.
        var result = await SubmitRawAsync(
            "The Matrix", "603", year: 1999, userId: Alice, username: "alice", autoApprove: true);
        Assert.True(result.IsSuccess);
        await DrainAsync();

        var request = Assert.Single(await ListAsync());
        Assert.Equal(MediaRequestStatus.Approved, request.Status);
        Assert.NotNull(request.WorkId);
        Assert.Equal(0, await PendingCountAsync());
        Assert.NotNull(await FindWorkAsync("603"));

        // Both events still travelled, so anything downstream (notifications, fulfilment) saw the pair.
        Assert.Single(_events.Requested);
        Assert.Single(_events.Approved);
    }

    [Fact]
    public async Task Without_auto_approval_a_request_still_waits()
    {
        await SubmitAsync("The Matrix", "603");
        await DrainAsync();

        Assert.Equal(MediaRequestStatus.Pending, (await ListAsync())[0].Status);
        Assert.Empty(_events.Approved);
        Assert.Null(await FindWorkAsync("603"));
    }

    [Fact]
    public async Task A_work_becoming_available_closes_its_request()
    {
        var requestId = await SubmitAsync("The Matrix", "603");
        await ApproveAsync(requestId);
        await DrainAsync();
        var workId = (await ListAsync())[0].WorkId!.Value;

        await PublishAsync(new WorkAvailable(workId, Guid.NewGuid()));
        await DrainAsync();

        Assert.Equal(MediaRequestStatus.Available, (await ListAsync())[0].Status);
    }

    [Fact]
    public async Task An_available_work_nobody_requested_closes_nothing()
    {
        await SubmitAsync("The Matrix", "603");
        await DrainAsync();

        await PublishAsync(new WorkAvailable(Guid.NewGuid(), Guid.NewGuid()));
        await DrainAsync();

        Assert.Equal(MediaRequestStatus.Pending, (await ListAsync())[0].Status);
    }

    [Fact]
    public async Task Rejecting_a_request_records_the_reason_and_catalogues_nothing()
    {
        var requestId = await SubmitAsync("The Matrix", "603");

        Assert.True((await RejectAsync(requestId, "Already own it on disc.")).IsSuccess);
        await DrainAsync();

        var request = Assert.Single(await ListAsync());
        Assert.Equal(MediaRequestStatus.Rejected, request.Status);
        Assert.Equal("Already own it on disc.", request.DecisionNote);
        Assert.Null(await FindWorkAsync("603"));
        Assert.Empty(_metadata.Refreshed);
    }

    [Fact]
    public async Task A_rejected_request_cannot_then_be_approved()
    {
        var requestId = await SubmitAsync("The Matrix", "603");
        await RejectAsync(requestId, reason: null);
        await DrainAsync();

        var approval = await ApproveAsync(requestId);

        Assert.True(approval.IsFailure);
        Assert.Equal("requests.already_decided", approval.Error.Code);
        Assert.Null(await FindWorkAsync("603"));
    }

    [Fact]
    public async Task Approving_twice_catalogues_the_title_once()
    {
        var requestId = await SubmitAsync("The Matrix", "603");
        await ApproveAsync(requestId);
        await DrainAsync();

        // A second approval is a no-op, not a second work.
        Assert.True((await ApproveAsync(requestId)).IsSuccess);
        await DrainAsync();

        var request = Assert.Single(await ListAsync());
        Assert.Equal(MediaRequestStatus.Approved, request.Status);
        Assert.Single(_metadata.Refreshed);
    }

    [Fact]
    public async Task The_same_title_cannot_be_requested_while_a_request_is_live()
    {
        await SubmitAsync("The Matrix", "603");

        var duplicate = await SubmitRawAsync("The Matrix", "603", year: null, userId: Guid.NewGuid(), username: "bob");

        Assert.True(duplicate.IsFailure);
        Assert.Equal("requests.duplicate", duplicate.Error.Code);
        Assert.Single(await ListAsync());
    }

    [Fact]
    public async Task Two_people_requesting_the_same_title_at_once_leaves_one_request()
    {
        // Both submissions read "nothing requested yet" before either writes; the partial unique index is
        // what decides, and the loser gets the same duplicate answer instead of a 500.
        var submissions = await Task.WhenAll(
            SubmitRawAsync("The Matrix", "603", year: null, userId: Alice, username: "alice"),
            SubmitRawAsync("The Matrix", "603", year: null, userId: Guid.NewGuid(), username: "bob"));

        Assert.Single(submissions, s => s.IsSuccess);
        var loser = Assert.Single(submissions, s => s.IsFailure);
        Assert.Equal("requests.duplicate", loser.Error.Code);
        Assert.Single(await ListAsync());
    }

    [Fact]
    public async Task A_rejected_title_can_be_requested_again()
    {
        var requestId = await SubmitAsync("The Matrix", "603");
        await RejectAsync(requestId, "Not right now.");
        await DrainAsync();

        var resubmitted = await SubmitRawAsync("The Matrix", "603", year: null, userId: Alice, username: "alice");

        Assert.True(resubmitted.IsSuccess);
        Assert.Equal(2, (await ListAsync()).Count);
        Assert.Equal(1, await PendingCountAsync());
    }

    [Fact]
    public async Task A_title_already_in_the_library_cannot_be_requested()
    {
        await AddMovieAsync("The Matrix", "603");

        var result = await SubmitRawAsync("The Matrix", "603", year: null, userId: Alice, username: "alice");

        Assert.True(result.IsFailure);
        Assert.Equal("requests.already_catalogued", result.Error.Code);
        Assert.Empty(await ListAsync());
    }

    [Fact]
    public async Task A_title_hidden_from_the_requester_is_requested_like_one_that_is_not_there()
    {
        // Arrange — the title is on a shelf Alice holds no grant for.
        var workId = await AddMovieAsync("The Matrix", "603");
        await using (var scope = _host.CreateAsyncScope())
        {
            var collections = scope.ServiceProvider.GetRequiredService<ICollectionAdministration>();
            var shelf = await collections.CreateAsync("Grown-ups", CollectionKind.Movies, CollectionAccessMode.Restricted);
            Assert.True((await collections.MoveWorkAsync(new WorkId(workId), shelf.Value)).IsSuccess);
        }

        // Act
        await using var submitScope = _host.CreateAsyncScope();
        var result = await submitScope.ServiceProvider.GetRequiredService<IMediaRequestCommands>().SubmitAsync(
            new SubmitMediaRequest("The Matrix", null, "tmdb", "603", Alice, "alice",
                Requester: new Viewer(Alice, IsAdministrator: false)));

        // Assert — no "already in your library": the administrator decides whether she gets it.
        Assert.True(result.IsSuccess, result.IsFailure ? result.Error.Code : null);
        Assert.Equal(1, await PendingCountAsync());
    }

    [Fact]
    public async Task A_trusted_requester_cannot_approve_themselves_into_a_title_hidden_from_them()
    {
        // Arrange — Alice decides her own requests, and the title is on a shelf she holds no grant for.
        var workId = await AddMovieAsync("The Matrix", "603");
        await using (var scope = _host.CreateAsyncScope())
        {
            var collections = scope.ServiceProvider.GetRequiredService<ICollectionAdministration>();
            var shelf = await collections.CreateAsync("Grown-ups", CollectionKind.Movies, CollectionAccessMode.Restricted);
            Assert.True((await collections.MoveWorkAsync(new WorkId(workId), shelf.Value)).IsSuccess);
        }

        // Act
        await using var submitScope = _host.CreateAsyncScope();
        var result = await submitScope.ServiceProvider.GetRequiredService<IMediaRequestCommands>().SubmitAsync(
            new SubmitMediaRequest("The Matrix", null, "tmdb", "603", Alice, "alice", AutoApprove: true,
                Requester: new Viewer(Alice, IsAdministrator: false)));
        await DrainAsync();

        // Assert — it waits for the administrator; approving would link it to the hidden work.
        Assert.True(result.IsSuccess);
        var request = Assert.Single(await ListAsync());
        Assert.Equal(MediaRequestStatus.Pending, request.Status);
        Assert.Null(request.WorkId);
        Assert.Empty(_events.Approved);
    }

    [Fact]
    public void A_request_linked_to_a_work_the_viewer_may_not_see_shows_neither_the_work_nor_that_it_arrived()
    {
        var hiddenWork = Guid.NewGuid();
        var arrived = new MediaRequest(
            MediaRequestId.New(), "The Matrix", 1999, "tmdb", "603", MediaRequestStatus.Available, Alice, "alice",
            hiddenWork, null, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);

        var seen = Api.RequestEndpoints.AsSeenBy(arrived, new HashSet<Guid>());
        var seenWithAccess = Api.RequestEndpoints.AsSeenBy(arrived, new HashSet<Guid> { hiddenWork });

        Assert.Null(seen.WorkId);
        Assert.Equal(MediaRequestStatus.Approved, seen.Status);
        Assert.Equal(arrived, seenWithAccess);
    }

    [Fact]
    public async Task A_failing_metadata_provider_still_fulfils_the_request()
    {
        var requestId = await SubmitAsync("The Matrix", "603");
        _metadata.Fail = true;

        await ApproveAsync(requestId);
        await DrainAsync();

        // Enrichment is best-effort: the title is catalogued and the request knows its work.
        var work = await FindWorkAsync("603");
        Assert.NotNull(work);
        Assert.Equal(work.Id.Value, (await ListAsync())[0].WorkId);
    }

    [Fact]
    public async Task A_user_only_sees_their_own_requests()
    {
        await SubmitAsync("The Matrix", "603");
        await SubmitRawAsync("Dune", "438631", year: 2021, userId: Guid.NewGuid(), username: "bob");
        await DrainAsync();

        var mine = await ListAsync(requestedBy: Alice);

        Assert.Equal("The Matrix", Assert.Single(mine).Title);
        Assert.Equal(2, (await ListAsync()).Count);
    }

    [Fact]
    public async Task Requests_can_be_filtered_by_status()
    {
        var matrix = await SubmitAsync("The Matrix", "603");
        await SubmitRawAsync("Dune", "438631", year: 2021, userId: Alice, username: "alice");
        await RejectAsync(matrix, "Not right now.");
        await DrainAsync();

        Assert.Equal("Dune", Assert.Single(await ListAsync(MediaRequestStatus.Pending)).Title);
        Assert.Equal("The Matrix", Assert.Single(await ListAsync(MediaRequestStatus.Rejected)).Title);
    }

    [Fact]
    public async Task Deciding_on_an_unknown_request_reports_not_found()
    {
        var approval = await ApproveAsync(new MediaRequestId(Guid.NewGuid()));
        Assert.True(approval.IsFailure);
        Assert.Equal("requests.not_found", approval.Error.Code);

        var rejection = await RejectAsync(new MediaRequestId(Guid.NewGuid()), "No such thing.");
        Assert.True(rejection.IsFailure);
        Assert.Equal("requests.not_found", rejection.Error.Code);
    }

    [Fact]
    public async Task An_approved_request_cannot_then_be_rejected()
    {
        var requestId = await SubmitAsync("The Matrix", "603");
        await ApproveAsync(requestId);
        await DrainAsync();

        var rejection = await RejectAsync(requestId, "Changed my mind.");

        Assert.True(rejection.IsFailure);
        Assert.Equal("requests.already_decided", rejection.Error.Code);
        Assert.Equal(MediaRequestStatus.Approved, (await ListAsync())[0].Status);
    }

    [Fact]
    public async Task Rejecting_twice_keeps_the_first_reason()
    {
        var requestId = await SubmitAsync("The Matrix", "603");
        await RejectAsync(requestId, "Already own it on disc.");
        await DrainAsync();

        // The repeat is a no-op, not a second decision that overwrites the note.
        Assert.True((await RejectAsync(requestId, "Something else entirely.")).IsSuccess);

        Assert.Equal("Already own it on disc.", (await ListAsync())[0].DecisionNote);
    }

    [Fact]
    public async Task A_request_must_carry_a_title_and_a_provider_we_can_link_to()
    {
        var blankTitle = await SubmitRawAsync("   ", "603", year: null, userId: Alice, username: "alice");
        Assert.Equal("requests.invalid_title", blankTitle.Error.Code);

        var noReference = await SubmitRawAsync("The Matrix", "  ", year: null, userId: Alice, username: "alice");
        Assert.Equal("requests.invalid_reference", noReference.Error.Code);

        // An unknown provider cannot be keyed on a work, which is what makes fulfilment idempotent.
        var unknownProvider = await SubmitWithProviderAsync("The Matrix", "hollywood", "603");
        Assert.True(unknownProvider.IsFailure);
        Assert.Equal("requests.invalid_reference", unknownProvider.Error.Code);

        // Enum.TryParse also accepts the numeric form, which would smuggle an undefined provider through.
        var numericProvider = await SubmitWithProviderAsync("The Matrix", "99", "603");
        Assert.True(numericProvider.IsFailure);
        Assert.Equal("requests.invalid_reference", numericProvider.Error.Code);

        Assert.Empty(await ListAsync());
    }

    [Fact]
    public async Task Approving_a_title_that_is_already_playable_closes_the_request_at_once()
    {
        // The operator added and downloaded the same title while the request sat pending: WorkAvailable
        // has already fired and never fires again, so fulfilment has to close the request itself.
        var requestId = await SubmitAsync("The Matrix", "603");
        var workId = await AddMovieAsync("The Matrix", "603");
        await MarkWorkAvailableAsync(workId);
        await DrainAsync();

        await ApproveAsync(requestId);
        await DrainAsync();

        var request = Assert.Single(await ListAsync());
        Assert.Equal(MediaRequestStatus.Available, request.Status);
        Assert.Equal(workId, request.WorkId);
    }

    [Fact]
    public async Task Approving_a_title_a_list_catalogued_unwatched_switches_its_watch_on()
    {
        // The trending list added the title as a suggestion while the request sat pending: Catalog hands
        // back the existing work and publishes no WorkAdded, so nothing else would ever start looking.
        var requestId = await SubmitAsync("The Matrix", "603");
        var workId = await AddMovieAsync("The Matrix", "603");
        _monitoring.SeedRoot(workId, monitored: false);

        await ApproveAsync(requestId);
        await DrainAsync();

        Assert.Equal((workId, MonitoringMode.All), Assert.Single(_monitoring.Applied));
        Assert.True(await _monitoring.IsMonitoredAsync(new WorkId(workId)));
    }

    [Fact]
    public async Task Approving_a_title_already_watched_leaves_its_policy_alone()
    {
        // A narrowed policy (say, only future episodes) is somebody's choice; a request must not widen it.
        var requestId = await SubmitAsync("The Matrix", "603");
        var workId = await AddMovieAsync("The Matrix", "603");
        _monitoring.SeedRoot(workId, monitored: true);

        await ApproveAsync(requestId);
        await DrainAsync();

        Assert.Empty(_monitoring.Applied);
    }

    [Fact]
    public async Task Approving_a_title_that_is_already_playable_starts_no_watch()
    {
        var requestId = await SubmitAsync("The Matrix", "603");
        var workId = await AddMovieAsync("The Matrix", "603");
        _monitoring.SeedRoot(workId, monitored: false);
        await MarkWorkAvailableAsync(workId);
        await DrainAsync();

        await ApproveAsync(requestId);
        await DrainAsync();

        Assert.Empty(_monitoring.Applied);

        // Redelivered (say the first run died enriching): the request is already Available, the work is
        // still playable, and the watch must stay off.
        await using (var scope = _host.CreateAsyncScope())
        {
            var handler = scope.ServiceProvider.GetRequiredService<ICommandHandler<FulfilMediaRequestCommand>>();
            Assert.True((await handler.HandleAsync(new FulfilMediaRequestCommand(requestId.Value))).IsSuccess);
        }

        Assert.Empty(_monitoring.Applied);
    }

    [Fact]
    public async Task The_database_refuses_a_second_live_request_for_the_same_title()
    {
        // The partial unique index is the backstop under the service's check: rejected rows are excluded,
        // so the same title can be requested again after a rejection but never twice while one is live.
        await SubmitAsync("The Matrix", "603");

        await using var scope = _host.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<RequestsDbContext>();
        dbContext.MediaRequests.Add(MediaRequestRecord.Create(
            new SubmitMediaRequest("The Matrix", 1999, "tmdb", "603", Guid.NewGuid(), "bob"), DateTimeOffset.UtcNow));

        var violation = await Assert.ThrowsAsync<DbUpdateException>(() => dbContext.SaveChangesAsync());
        Assert.IsType<PostgresException>(violation.InnerException);
        Assert.Equal(PostgresErrorCodes.UniqueViolation, ((PostgresException)violation.InnerException!).SqlState);
    }

    [Fact]
    public async Task Submitting_publishes_the_event_the_rest_of_the_platform_reacts_to()
    {
        await SubmitAsync("The Matrix", "603", year: 1999);
        await DrainAsync();

        var requested = Assert.Single(_events.Requested);
        Assert.Equal("The Matrix", requested.Title);
        Assert.Equal(1999, requested.Year);
        Assert.Equal("tmdb", requested.Provider);
        Assert.Equal("603", requested.ExternalId);
        Assert.Equal(Alice, requested.RequestedByUserId);
        Assert.Equal("alice", requested.RequestedByUsername);
    }

    [Fact]
    public async Task Rejecting_publishes_the_decision_with_its_reason()
    {
        var requestId = await SubmitAsync("The Matrix", "603");
        await RejectAsync(requestId, "Already own it on disc.");
        await DrainAsync();

        var rejected = Assert.Single(_events.Rejected);
        Assert.Equal(requestId.Value, rejected.RequestId);
        Assert.Equal("The Matrix", rejected.Title);
        Assert.Equal(Alice, rejected.RequestedByUserId);
        Assert.Equal("Already own it on disc.", rejected.Reason);
    }

    [Fact]
    public async Task The_list_is_newest_first_and_its_limit_is_bounded()
    {
        await SubmitAsync("The Matrix", "603");
        await SubmitRawAsync("Dune", "438631", year: 2021, userId: Alice, username: "alice");
        await DrainAsync();

        Assert.Equal("Dune", (await ListAsync())[0].Title);

        // A nonsensical limit is clamped, not passed to the database as-is.
        await using var scope = _host.CreateAsyncScope();
        var query = scope.ServiceProvider.GetRequiredService<IMediaRequestQuery>();
        Assert.Single(await query.ListAsync(null, null, 0));
        Assert.Equal(2, (await query.ListAsync(null, null, 10_000)).Count);
    }

    // -- helpers ---------------------------------------------------------------------------------

    private async Task<MediaRequestId> SubmitAsync(string title, string externalId, int? year = null)
    {
        var result = await SubmitRawAsync(title, externalId, year, Alice, "alice");
        Assert.True(result.IsSuccess, result.Error.Message);
        return result.Value;
    }

    private async Task<Result<MediaRequestId>> SubmitRawAsync(
        string title,
        string externalId,
        int? year,
        Guid userId,
        string username,
        bool autoApprove = false)
    {
        await using var scope = _host.CreateAsyncScope();
        var commands = scope.ServiceProvider.GetRequiredService<IMediaRequestCommands>();
        return await commands.SubmitAsync(
            new SubmitMediaRequest(title, year, "tmdb", externalId, userId, username, autoApprove,
                Requester: new Viewer(userId, IsAdministrator: false)));
    }

    private async Task<MediaRequestId> SubmitKindAsync(string title, string externalId, MediaRequestKind kind)
    {
        var result = await SubmitKindResultAsync(title, externalId, kind);
        Assert.True(result.IsSuccess, result.IsSuccess ? null : result.Error.Message);
        return result.Value;
    }

    private async Task<Result<MediaRequestId>> SubmitKindResultAsync(string title, string externalId, MediaRequestKind kind)
    {
        await using var scope = _host.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<IMediaRequestCommands>().SubmitAsync(
            new SubmitMediaRequest(title, null, "tmdb", externalId, Alice, "alice", Kind: kind,
                Requester: new Viewer(Alice, IsAdministrator: false)));
    }

    private async Task<Result> ApproveAsync(MediaRequestId id)
    {
        await using var scope = _host.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<IMediaRequestCommands>().ApproveAsync(id, Operator);
    }

    private async Task<Result> RejectAsync(MediaRequestId id, string? reason)
    {
        await using var scope = _host.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<IMediaRequestCommands>().RejectAsync(id, Operator, reason);
    }

    private async Task<IReadOnlyList<MediaRequest>> ListAsync(
        MediaRequestStatus? status = null,
        Guid? requestedBy = null)
    {
        await using var scope = _host.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<IMediaRequestQuery>().ListAsync(status, requestedBy, 50);
    }

    private async Task<int> PendingCountAsync()
    {
        await using var scope = _host.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<IMediaRequestQuery>().PendingCountAsync();
    }

    private async Task<WorkSummary?> FindWorkAsync(string externalId)
    {
        await using var scope = _host.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<ICatalogQuery>()
            .FindByExternalIdAsync(MetadataProvider.Tmdb, externalId);
    }

    private async Task<Guid> AddMovieAsync(string title, string externalId)
    {
        await using var scope = _host.CreateAsyncScope();
        var added = await scope.ServiceProvider.GetRequiredService<ICatalogCommands>()
            .AddMovieAsync(title, null, [new ExternalId(MetadataProvider.Tmdb, externalId)]);
        return added.Value.Value;
    }

    /// <summary>Marks a work playable the way Import does, which is what publishes WorkAvailable — once.</summary>
    private async Task MarkWorkAvailableAsync(Guid workId)
    {
        await using var scope = _host.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<ICatalogCommands>()
            .MarkWorkAvailableAsync(workId, Guid.NewGuid());
    }

    private async Task<Result<MediaRequestId>> SubmitWithProviderAsync(string title, string provider, string externalId)
    {
        await using var scope = _host.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<IMediaRequestCommands>().SubmitAsync(
            new SubmitMediaRequest(title, null, provider, externalId, Alice, "alice",
                Requester: new Viewer(Alice, IsAdministrator: false)));
    }

    private async Task PublishAsync(IDomainEvent domainEvent)
    {
        await using var scope = _host.CreateAsyncScope();
        var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
        var eventBus = scope.ServiceProvider.GetRequiredService<IEventBus>();
        await unitOfWork.ExecuteAsync(async token => await eventBus.PublishAsync(domainEvent, token));
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
