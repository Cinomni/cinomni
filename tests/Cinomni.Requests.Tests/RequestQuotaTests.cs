using Cinomni.Requests.Contracts;
using Cinomni.Requests.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Cinomni.Requests.Tests;

/// <summary>
/// How much of the administrator's decision queue one account may occupy at once.
/// <para>
/// The property every one of these is really about is that the cap <em>releases</em>. A limit that only
/// ever tightened would be a lifetime allowance wearing a concurrency limit's name, and the household
/// would hit it once and never ask for anything again.
/// </para>
/// </summary>
public sealed class RequestQuotaTests : IAsyncLifetime
{
    private static readonly Guid Alice = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid Bob = Guid.Parse("22222222-2222-2222-2222-222222222222");

    private readonly FakeMetadataRefresh _metadata = new();
    private readonly RequestEventSink _events = new();
    private ServiceProvider? _provider;

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        if (_provider is not null)
        {
            await _provider.DisposeAsync();
        }
    }

    [Fact]
    public async Task An_installation_that_sets_no_limit_is_unchanged()
    {
        await StartAsync("cinomni_test_requests_quota_off", defaultLimit: 0);

        // Zero is the shipped default, and it has to mean exactly what every installation does today.
        // An upgrade that silently capped a household would be the feature discovering itself for them.
        for (var i = 0; i < 12; i++)
        {
            Assert.True((await SubmitAsync($"Title {i}", $"{i}")).IsSuccess);
        }
    }

    [Fact]
    public async Task A_full_queue_refuses_the_next_request_and_says_how_full()
    {
        await StartAsync("cinomni_test_requests_quota_full", defaultLimit: 2);

        Assert.True((await SubmitAsync("One", "1")).IsSuccess);
        Assert.True((await SubmitAsync("Two", "2")).IsSuccess);

        var refused = await SubmitAsync("Three", "3");

        Assert.True(refused.IsFailure);
        Assert.Equal("requests.quota_exceeded", refused.Error.Code);
        // The number is in the message: a member told "you have 2 of 2 open" understands a rule, and
        // one told "no" understands a wall.
        Assert.Contains("2 of 2", refused.Error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_rejected_request_gives_the_slot_back()
    {
        await StartAsync("cinomni_test_requests_quota_rejected", defaultLimit: 1);

        var first = await SubmitAsync("One", "1");
        Assert.True((await SubmitAsync("Two", "2")).IsFailure);

        await using (var scope = _provider!.CreateAsyncScope())
        {
            var commands = scope.ServiceProvider.GetRequiredService<IMediaRequestCommands>();
            Assert.True((await commands.RejectAsync(first.Value, Bob, "Not for us.")).IsSuccess);
        }

        // Being turned down must not spend the allowance. Otherwise a member is punished for a
        // decision somebody else made, and the fair thing looks identical to the unfair one.
        Assert.True((await SubmitAsync("Two", "2")).IsSuccess);
    }

    [Fact]
    public async Task A_fulfilled_request_gives_the_slot_back()
    {
        await StartAsync("cinomni_test_requests_quota_fulfilled", defaultLimit: 1);

        var first = await SubmitAsync("One", "1");
        await using (var scope = _provider!.CreateAsyncScope())
        {
            var commands = scope.ServiceProvider.GetRequiredService<IMediaRequestCommands>();
            Assert.True((await commands.ApproveAsync(first.Value, Bob)).IsSuccess);
        }

        // Approved and still waiting is open: it is occupying nothing of the decision queue but it is
        // occupying the household's attention, and it is what the member asked for.
        Assert.True((await SubmitAsync("Two", "2")).IsFailure);

        await MarkAvailableAsync(first.Value);

        // And once it is playable the request is finished, so the slot comes back. This is the whole
        // reason this is a concurrency limit and not an allowance: without it the cap only tightens.
        Assert.True((await SubmitAsync("Two", "2")).IsSuccess);
    }

    [Fact]
    public async Task The_cap_is_per_account_and_not_shared()
    {
        await StartAsync("cinomni_test_requests_quota_per_account", defaultLimit: 1);

        Assert.True((await SubmitAsync("One", "1")).IsSuccess);
        Assert.True((await SubmitAsync("Two", "2")).IsFailure);

        // One person filling their share is the situation this exists for; it must not become one
        // person filling everybody else's.
        Assert.True((await SubmitAsync("Two", "2", user: Bob, username: "bob")).IsSuccess);
    }

    [Fact]
    public async Task An_account_limit_overrides_the_installation_default_in_both_directions()
    {
        await StartAsync("cinomni_test_requests_quota_override", defaultLimit: 1);

        // Tighter than the house rule is not the interesting direction; looser is, because zero is
        // how an administrator lifts the cap for one person without lifting it for everyone.
        Assert.True((await SubmitAsync("One", "1", limit: 0)).IsSuccess);
        Assert.True((await SubmitAsync("Two", "2", limit: 0)).IsSuccess);
        Assert.True((await SubmitAsync("Three", "3", limit: 0)).IsSuccess);

        // And the same account is refused the moment it is asked to obey a number it is already over.
        var refused = await SubmitAsync("Four", "4", limit: 2);
        Assert.True(refused.IsFailure);
        Assert.Equal("requests.quota_exceeded", refused.Error.Code);
    }

    // -- helpers ---------------------------------------------------------------------------------

    private async Task StartAsync(string database, int defaultLimit)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Requests:DefaultOpenRequestLimit"] = defaultLimit.ToString(),
            })
            .Build();

        _provider = await RequestsTestHost.CreateAsync(database, _metadata, _events, configuration);
    }

    private async Task<Cinomni.Kernel.Results.Result<MediaRequestId>> SubmitAsync(
        string title, string externalId, Guid? user = null, string username = "alice", int? limit = null)
    {
        await using var scope = _provider!.CreateAsyncScope();
        var commands = scope.ServiceProvider.GetRequiredService<IMediaRequestCommands>();
        return await commands.SubmitAsync(new SubmitMediaRequest(
            title, null, "Tmdb", externalId, user ?? Alice, username, AutoApprove: false, OpenRequestLimit: limit));
    }

    /// <summary>Drives the record to its terminal state the way the availability handler does.</summary>
    private async Task MarkAvailableAsync(MediaRequestId id)
    {
        await using var scope = _provider!.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<RequestsDbContext>();
        var record = await dbContext.MediaRequests.SingleAsync(r => r.Id == id.Value);
        Assert.True(record.MarkAvailable());
        await dbContext.SaveChangesAsync();
    }
}
