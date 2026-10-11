using Cinomni.Identity.Application;
using Cinomni.Identity.Contracts;
using Cinomni.Identity.Events;
using Cinomni.Identity.Persistence;
using Cinomni.Operations.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Cinomni.Identity.Tests;

/// <summary>
/// An account changing its own password, against a real PostgreSQL. What matters is what a change
/// leaves behind: only the new password signs in, only the session that made it stays open, and a
/// refused change leaves everything exactly as it was.
/// </summary>
public sealed class PasswordChangeTests : IAsyncLifetime
{
    private const string Password = "correct-horse";
    private const string NewPassword = "battery-staple";

    private ServiceProvider _provider = null!;

    public async Task InitializeAsync() =>
        _provider = await IdentityTestHost.CreateAsync("cinomni_test_identity_password_change");

    public async Task DisposeAsync() => await _provider.DisposeAsync();

    [Fact]
    public async Task A_changed_password_signs_in_and_the_old_one_no_longer_does()
    {
        var userId = await CreateAdminAsync();
        var token = await IssueSessionAsync(userId);

        var changed = await ChangeAsync(userId, Password, NewPassword, token);

        Assert.True(changed.IsSuccess, changed.IsSuccess ? null : changed.Error.Message);
        await using var scope = _provider.CreateAsyncScope();
        var authenticator = scope.ServiceProvider.GetRequiredService<IAuthenticator>();
        Assert.True((await authenticator.AuthenticateAsync("admin", NewPassword)).IsSuccess);
        Assert.True((await authenticator.AuthenticateAsync("admin", Password)).IsFailure);
    }

    [Fact]
    public async Task Every_other_session_ends_and_the_one_that_made_the_change_stays()
    {
        var userId = await CreateAdminAsync();
        var kept = await IssueSessionAsync(userId);
        var other = await IssueSessionAsync(userId);

        var changed = await ChangeAsync(userId, Password, NewPassword, kept);

        Assert.True(changed.IsSuccess);
        await using var scope = _provider.CreateAsyncScope();
        var sessions = scope.ServiceProvider.GetRequiredService<ISessionService>();
        Assert.True((await sessions.ValidateAsync(kept)).IsSuccess);
        Assert.True((await sessions.ValidateAsync(other)).IsFailure);
    }

    [Fact]
    public async Task A_wrong_current_password_changes_nothing()
    {
        var userId = await CreateAdminAsync();
        var kept = await IssueSessionAsync(userId);
        var other = await IssueSessionAsync(userId);

        var refused = await ChangeAsync(userId, "not-the-password", NewPassword, kept);

        Assert.True(refused.IsFailure);
        Assert.Equal("identity.invalid_credentials", refused.Error.Code);
        await using var scope = _provider.CreateAsyncScope();
        Assert.True((await scope.ServiceProvider.GetRequiredService<IAuthenticator>()
            .AuthenticateAsync("admin", Password)).IsSuccess);
        Assert.True((await scope.ServiceProvider.GetRequiredService<ISessionService>().ValidateAsync(other)).IsSuccess);
        Assert.Equal(0, await OutboxCountAsync(scope, IdentityEventNames.PasswordChanged));
    }

    [Fact]
    public async Task The_policy_is_only_answered_once_the_current_password_holds()
    {
        var userId = await CreateAdminAsync();
        var token = await IssueSessionAsync(userId);

        // A weak new password behind a wrong current one reads as the wrong password, not the weak one:
        // a borrowed session learns nothing about the policy, or about whether it guessed right.
        var probe = await ChangeAsync(userId, "not-the-password", "short", token);

        Assert.Equal("identity.invalid_credentials", probe.Error.Code);
    }

    [Theory]
    [InlineData("short", "identity.weak_password")]
    [InlineData(Password, "identity.password_unchanged")]
    public async Task A_new_password_the_rules_refuse_is_not_set(string newPassword, string expectedCode)
    {
        var userId = await CreateAdminAsync();
        var token = await IssueSessionAsync(userId);

        var refused = await ChangeAsync(userId, Password, newPassword, token);

        Assert.Equal(expectedCode, refused.Error.Code);
        await using var scope = _provider.CreateAsyncScope();
        Assert.True((await scope.ServiceProvider.GetRequiredService<IAuthenticator>()
            .AuthenticateAsync("admin", Password)).IsSuccess);
    }

    [Fact]
    public async Task A_new_password_past_the_ceiling_is_refused_before_it_is_hashed()
    {
        var userId = await CreateAdminAsync();
        var token = await IssueSessionAsync(userId);

        var refused = await ChangeAsync(userId, Password, new string('x', 1025), token);

        Assert.Equal("identity.password_too_long", refused.Error.Code);
    }

    [Fact]
    public async Task A_disabled_account_cannot_change_its_password()
    {
        var adminId = await CreateAdminAsync();
        Guid memberId;
        await using (var scope = _provider.CreateAsyncScope())
        {
            var provisioning = scope.ServiceProvider.GetRequiredService<IUserProvisioning>();
            memberId = (await provisioning.CreateUserAsync("member", Password, UserRole.Member)).Value.Value;
            Assert.True((await provisioning.SetDisabledAsync(new UserId(memberId), disabled: true)).IsSuccess);
        }

        var refused = await ChangeAsync(new UserId(memberId), Password, NewPassword, await IssueSessionAsync(adminId));

        Assert.Equal("identity.invalid_credentials", refused.Error.Code);
    }

    [Fact]
    public async Task A_sign_in_waiting_for_its_code_does_not_survive_the_change()
    {
        var userId = await CreateAdminAsync();
        var token = await IssueSessionAsync(userId);
        await using (var scope = _provider.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<ITwoFactorService>().IssueChallengeAsync(userId);
        }

        Assert.True((await ChangeAsync(userId, Password, NewPassword, token)).IsSuccess);

        await using (var scope = _provider.CreateAsyncScope())
        {
            var open = await scope.ServiceProvider.GetRequiredService<IdentityDbContext>()
                .LoginChallenges.CountAsync(c => c.UserId == userId.Value && c.ConsumedAt == null);
            Assert.Equal(0, open);
        }
    }

    [Fact]
    public async Task The_change_and_its_event_commit_together()
    {
        var userId = await CreateAdminAsync();

        Assert.True((await ChangeAsync(userId, Password, NewPassword, await IssueSessionAsync(userId))).IsSuccess);

        await using var scope = _provider.CreateAsyncScope();
        Assert.Equal(1, await OutboxCountAsync(scope, IdentityEventNames.PasswordChanged));
    }

    // -- helpers ---------------------------------------------------------------------------------

    private async Task<UserId> CreateAdminAsync()
    {
        await using var scope = _provider.CreateAsyncScope();
        var created = await scope.ServiceProvider.GetRequiredService<IUserProvisioning>().CreateAdminAsync("admin", Password);
        Assert.True(created.IsSuccess, created.IsSuccess ? null : created.Error.Message);
        return created.Value;
    }

    private async Task<string> IssueSessionAsync(UserId userId)
    {
        await using var scope = _provider.CreateAsyncScope();
        return (await scope.ServiceProvider.GetRequiredService<ISessionService>().IssueAsync(userId)).Token;
    }

    private async Task<Cinomni.Kernel.Results.Result> ChangeAsync(
        UserId userId, string current, string next, string token)
    {
        await using var scope = _provider.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<IPasswordChangeService>()
            .ChangeAsync(userId, current, next, token);
    }

    private static Task<int> OutboxCountAsync(AsyncServiceScope scope, string eventType) =>
        scope.ServiceProvider.GetRequiredService<OperationsDbContext>().Outbox.CountAsync(m => m.EventType == eventType);
}
