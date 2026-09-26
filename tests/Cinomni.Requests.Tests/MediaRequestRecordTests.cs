using Cinomni.Requests.Contracts;
using Cinomni.Requests.Persistence;

namespace Cinomni.Requests.Tests;

/// <summary>
/// Unit tests for the request state machine — the guard that makes every decision a one-way transition, so
/// a redelivered command can never decide twice. Pure domain logic: no database.
/// </summary>
public sealed class MediaRequestRecordTests
{
    private static readonly DateTimeOffset Now = new(2026, 7, 28, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void A_new_request_starts_pending_and_undecided()
    {
        var request = Create();

        Assert.Equal(MediaRequestStatus.Pending, request.Status);
        Assert.Null(request.WorkId);
        Assert.Null(request.DecidedAt);
        Assert.Null(request.DecidedByUserId);
    }

    [Fact]
    public void Approving_a_pending_request_records_who_decided_and_when()
    {
        var request = Create();
        var approver = Guid.NewGuid();

        Assert.True(request.Approve(approver, Now));

        Assert.Equal(MediaRequestStatus.Approved, request.Status);
        Assert.Equal(approver, request.DecidedByUserId);
        Assert.Equal(Now, request.DecidedAt);
    }

    [Fact]
    public void Approving_an_already_decided_request_does_nothing()
    {
        var request = Create();
        request.Reject(Guid.NewGuid(), "Not for the family library.", Now);

        Assert.False(request.Approve(Guid.NewGuid(), Now.AddMinutes(1)));
        Assert.Equal(MediaRequestStatus.Rejected, request.Status);
    }

    [Fact]
    public void Rejecting_keeps_the_reason_and_blanks_an_empty_one()
    {
        var withReason = Create();
        withReason.Reject(Guid.NewGuid(), "  Already own it on disc.  ", Now);
        Assert.Equal("Already own it on disc.", withReason.DecisionNote);

        var withoutReason = Create();
        withoutReason.Reject(Guid.NewGuid(), "   ", Now);
        Assert.Null(withoutReason.DecisionNote);
    }

    [Fact]
    public void Attaching_the_same_work_twice_is_a_no_op()
    {
        var request = Create();
        request.Approve(Guid.NewGuid(), Now);
        var workId = Guid.NewGuid();

        Assert.True(request.AttachWork(workId));
        Assert.False(request.AttachWork(workId));
        Assert.Equal(workId, request.WorkId);
    }

    [Fact]
    public void Only_an_approved_request_becomes_available()
    {
        var pending = Create();
        Assert.False(pending.MarkAvailable());
        Assert.Equal(MediaRequestStatus.Pending, pending.Status);

        var approved = Create();
        approved.Approve(Guid.NewGuid(), Now);
        Assert.True(approved.MarkAvailable());
        Assert.Equal(MediaRequestStatus.Available, approved.Status);

        // Already available — the redelivered event changes nothing.
        Assert.False(approved.MarkAvailable());
    }

    [Fact]
    public void The_provider_name_is_normalized_and_the_title_trimmed()
    {
        var request = MediaRequestRecord.Create(
            new SubmitMediaRequest("  The Matrix  ", 1999, "TMDB", " 603 ", Guid.NewGuid(), " Alice "),
            Now);

        Assert.Equal("The Matrix", request.Title);
        Assert.Equal("tmdb", request.Provider);
        Assert.Equal("603", request.ExternalId);
        Assert.Equal("Alice", request.RequestedByUsername);
    }

    [Fact]
    public void An_oversized_rejection_reason_is_truncated_to_the_column_width()
    {
        var request = Create();

        request.Reject(Guid.NewGuid(), new string('x', 900), Now);

        Assert.Equal(500, request.DecisionNote!.Length);
    }

    private static MediaRequestRecord Create() => MediaRequestRecord.Create(
        new SubmitMediaRequest("The Matrix", 1999, "tmdb", "603", Guid.NewGuid(), "alice"),
        Now);
}
