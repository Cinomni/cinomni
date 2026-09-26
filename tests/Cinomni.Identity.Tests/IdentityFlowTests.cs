using Cinomni.Identity.Application;
using Cinomni.Identity.Contracts;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Cinomni.Identity.Tests;

/// <summary>
/// Integration tests for the Identity flow against a real PostgreSQL instance: first-run
/// setup, login, session issue/validate/revoke, and the setup-closed and bad-password paths.
/// </summary>
public sealed class IdentityFlowTests : IAsyncLifetime
{
    private ServiceProvider _provider = null!;

    public async Task InitializeAsync() =>
        _provider = await IdentityTestHost.CreateAsync("cinomni_test_identity");

    public async Task DisposeAsync() => await _provider.DisposeAsync();

    [Fact]
    public async Task Full_flow_setup_login_validate_revoke()
    {
        // First-run: no accounts yet, then create the admin.
        await using (var scope = _provider.CreateAsyncScope())
        {
            var provisioning = scope.ServiceProvider.GetRequiredService<IUserProvisioning>();
            Assert.False(await provisioning.AnyUserExistsAsync());

            var created = await provisioning.CreateAdminAsync("Admin", "correct-horse");
            Assert.True(created.IsSuccess);
            Assert.True(await provisioning.AnyUserExistsAsync());
        }

        // Login: credentials are validated (username is case-insensitive).
        UserId userId;
        await using (var scope = _provider.CreateAsyncScope())
        {
            var authenticator = scope.ServiceProvider.GetRequiredService<IAuthenticator>();
            var authenticated = await authenticator.AuthenticateAsync("admin", "correct-horse");

            Assert.True(authenticated.IsSuccess);
            Assert.Equal("admin", authenticated.Value.Username);
            Assert.True(authenticated.Value.IsAdministrator);
            userId = authenticated.Value.Id;
        }

        // Issue a session token and validate it.
        string token;
        await using (var scope = _provider.CreateAsyncScope())
        {
            var sessions = scope.ServiceProvider.GetRequiredService<ISessionService>();
            var issued = await sessions.IssueAsync(userId);
            token = issued.Token;
            Assert.True(issued.ExpiresAt > DateTimeOffset.UtcNow);

            var validated = await sessions.ValidateAsync(token);
            Assert.True(validated.IsSuccess);
            Assert.Equal(userId, validated.Value.Id);
        }

        // Revoke it; the token no longer validates.
        await using (var scope = _provider.CreateAsyncScope())
        {
            var sessions = scope.ServiceProvider.GetRequiredService<ISessionService>();
            Assert.True(await sessions.RevokeAsync(token));

            var validated = await sessions.ValidateAsync(token);
            Assert.True(validated.IsFailure);
            Assert.Equal("identity.invalid_token", validated.Error.Code);
        }
    }

    [Fact]
    public async Task Setup_is_closed_after_the_first_account()
    {
        await using var scope = _provider.CreateAsyncScope();
        var provisioning = scope.ServiceProvider.GetRequiredService<IUserProvisioning>();

        Assert.True((await provisioning.CreateAdminAsync("admin", "correct-horse")).IsSuccess);

        var second = await provisioning.CreateAdminAsync("intruder", "another-pass");
        Assert.True(second.IsFailure);
        Assert.Equal("identity.setup_already_completed", second.Error.Code);
    }

    [Fact]
    public async Task Login_fails_with_a_generic_error_on_wrong_password()
    {
        await using (var setupScope = _provider.CreateAsyncScope())
        {
            var provisioning = setupScope.ServiceProvider.GetRequiredService<IUserProvisioning>();
            await provisioning.CreateAdminAsync("admin", "correct-horse");
        }

        await using var scope = _provider.CreateAsyncScope();
        var authenticator = scope.ServiceProvider.GetRequiredService<IAuthenticator>();

        var wrongPassword = await authenticator.AuthenticateAsync("admin", "wrong-password");
        Assert.True(wrongPassword.IsFailure);
        Assert.Equal("identity.invalid_credentials", wrongPassword.Error.Code);

        var unknownUser = await authenticator.AuthenticateAsync("ghost", "whatever");
        Assert.True(unknownUser.IsFailure);
        Assert.Equal("identity.invalid_credentials", unknownUser.Error.Code);
    }

    [Fact]
    public async Task An_administrator_can_invite_further_accounts()
    {
        await using var scope = _provider.CreateAsyncScope();
        var provisioning = scope.ServiceProvider.GetRequiredService<IUserProvisioning>();
        await provisioning.CreateAdminAsync("admin", "correct-horse");

        var member = await provisioning.CreateUserAsync("Alice", "battery-staple", UserRole.Member);
        Assert.True(member.IsSuccess);

        var accounts = await provisioning.ListAsync();
        Assert.Equal(["admin", "alice"], accounts.Select(a => a.Username));
        Assert.Equal([UserRole.Administrator, UserRole.Member], accounts.Select(a => a.Role));

        // A member may request by default; an administrator's permissions are implicit and always full.
        Assert.Equal(UserPermissions.Default, accounts[1].Permissions);
        Assert.Equal(UserPermissions.Full, accounts[0].Permissions);

        // The new account logs in like any other, without administrator rights.
        var authenticated = await scope.ServiceProvider.GetRequiredService<IAuthenticator>()
            .AuthenticateAsync("alice", "battery-staple");
        Assert.True(authenticated.IsSuccess);
        Assert.False(authenticated.Value.IsAdministrator);
    }

    [Fact]
    public async Task An_account_can_be_promoted_demoted_and_disabled()
    {
        await using var scope = _provider.CreateAsyncScope();
        var provisioning = scope.ServiceProvider.GetRequiredService<IUserProvisioning>();
        var authenticator = scope.ServiceProvider.GetRequiredService<IAuthenticator>();
        await provisioning.CreateAdminAsync("admin", "correct-horse");
        var alice = (await provisioning.CreateUserAsync("alice", "battery-staple", UserRole.Member)).Value;

        Assert.True((await provisioning.SetRoleAsync(alice, UserRole.Administrator)).IsSuccess);
        var promoted = await authenticator.AuthenticateAsync("alice", "battery-staple");
        Assert.True(promoted.Value.IsAdministrator);
        Assert.Equal(UserPermissions.Full, promoted.Value.Permissions);

        Assert.True((await provisioning.SetRoleAsync(alice, UserRole.Member)).IsSuccess);
        Assert.False((await authenticator.AuthenticateAsync("alice", "battery-staple")).Value.IsAdministrator);

        // A disabled account cannot sign in, and re-enabling restores it.
        Assert.True((await provisioning.SetDisabledAsync(alice, disabled: true)).IsSuccess);
        Assert.True((await authenticator.AuthenticateAsync("alice", "battery-staple")).IsFailure);
        Assert.True((await provisioning.SetDisabledAsync(alice, disabled: false)).IsSuccess);
        Assert.True((await authenticator.AuthenticateAsync("alice", "battery-staple")).IsSuccess);
    }

    [Fact]
    public async Task The_installation_always_keeps_an_administrator_who_can_sign_in()
    {
        await using var scope = _provider.CreateAsyncScope();
        var provisioning = scope.ServiceProvider.GetRequiredService<IUserProvisioning>();
        var admin = (await provisioning.CreateAdminAsync("admin", "correct-horse")).Value;
        await provisioning.CreateUserAsync("alice", "battery-staple", UserRole.Member);

        // The only administrator can be neither demoted nor disabled — that would lock everyone out.
        var demoted = await provisioning.SetRoleAsync(admin, UserRole.Member);
        Assert.Equal("identity.last_administrator", demoted.Error.Code);
        var disabled = await provisioning.SetDisabledAsync(admin, disabled: true);
        Assert.Equal("identity.last_administrator", disabled.Error.Code);

        // With a second administrator in place, both become possible.
        var alice = (await provisioning.ListAsync()).Single(a => a.Username == "alice").Id;
        await provisioning.SetRoleAsync(alice, UserRole.Administrator);
        Assert.True((await provisioning.SetRoleAsync(admin, UserRole.Member)).IsSuccess);

        // ...but not once that second administrator is the last one standing.
        Assert.Equal(
            "identity.last_administrator",
            (await provisioning.SetDisabledAsync(alice, disabled: true)).Error.Code);
    }

    [Fact]
    public async Task Member_permissions_are_settable_and_an_administrators_are_implicit()
    {
        await using var scope = _provider.CreateAsyncScope();
        var provisioning = scope.ServiceProvider.GetRequiredService<IUserProvisioning>();
        var admin = (await provisioning.CreateAdminAsync("admin", "correct-horse")).Value;
        var alice = (await provisioning.CreateUserAsync("alice", "battery-staple", UserRole.Member)).Value;

        var trusted = new UserPermissions(CanRequest: true, RequestsAutoApproved: true);
        Assert.True((await provisioning.SetPermissionsAsync(alice, trusted)).IsSuccess);
        Assert.Equal(trusted, (await provisioning.ListAsync()).Single(a => a.Username == "alice").Permissions);

        var viewer = new UserPermissions(CanRequest: false, RequestsAutoApproved: false);
        Assert.True((await provisioning.SetPermissionsAsync(alice, viewer)).IsSuccess);
        Assert.Equal(viewer, (await provisioning.ListAsync()).Single(a => a.Username == "alice").Permissions);

        // An administrator has them all by definition, so setting them is refused rather than ignored.
        var refused = await provisioning.SetPermissionsAsync(admin, viewer);
        Assert.Equal("identity.administrator_permissions", refused.Error.Code);
    }

    [Fact]
    public async Task Administering_an_unknown_account_reports_not_found()
    {
        await using var scope = _provider.CreateAsyncScope();
        var provisioning = scope.ServiceProvider.GetRequiredService<IUserProvisioning>();
        await provisioning.CreateAdminAsync("admin", "correct-horse");
        var ghost = UserId.New();

        Assert.Equal("identity.user_not_found", (await provisioning.SetRoleAsync(ghost, UserRole.Member)).Error.Code);
        Assert.Equal("identity.user_not_found", (await provisioning.SetDisabledAsync(ghost, true)).Error.Code);
        Assert.Equal(
            "identity.user_not_found",
            (await provisioning.SetPermissionsAsync(ghost, UserPermissions.Default)).Error.Code);
    }

    [Fact]
    public async Task A_taken_username_and_a_weak_password_are_both_refused()
    {
        await using var scope = _provider.CreateAsyncScope();
        var provisioning = scope.ServiceProvider.GetRequiredService<IUserProvisioning>();
        await provisioning.CreateAdminAsync("admin", "correct-horse");
        await provisioning.CreateUserAsync("alice", "battery-staple", UserRole.Member);

        // Usernames are matched case-insensitively, so "Alice" is the same account.
        var taken = await provisioning.CreateUserAsync("Alice", "another-pass", UserRole.Member);
        Assert.True(taken.IsFailure);
        Assert.Equal("identity.username_taken", taken.Error.Code);

        var weak = await provisioning.CreateUserAsync("bob", "short", UserRole.Member);
        Assert.True(weak.IsFailure);
        Assert.Equal("identity.weak_password", weak.Error.Code);
    }
}
