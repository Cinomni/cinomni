using Cinomni.Identity.Application;
using Cinomni.Identity.Contracts;
using Cinomni.Identity.Security;
using Microsoft.Extensions.DependencyInjection;

namespace Cinomni.Identity.Tests;

/// <summary>
/// The account rules that span several rows or trust a value from the wire: one first administrator,
/// never zero administrators, roles and limits that mean something, and a sign-in whose timing does not
/// tell a disabled account from a wrong password.
/// </summary>
public sealed class AccountHardeningTests : IAsyncLifetime
{
    private readonly CountingHasher _hasher = new();
    private ServiceProvider _provider = null!;

    public async Task InitializeAsync() =>
        _provider = await IdentityTestHost.CreateAsync(
            "cinomni_test_identity_hardening",
            services => services.AddSingleton<IPasswordHasher>(_hasher));

    public async Task DisposeAsync() => await _provider.DisposeAsync();

    [Fact]
    public async Task Two_setup_requests_at_once_create_one_administrator()
    {
        var attempts = await Task.WhenAll(Enumerable.Range(0, 6).Select(async i =>
        {
            await using var scope = _provider.CreateAsyncScope();
            return await scope.ServiceProvider.GetRequiredService<IUserProvisioning>()
                .CreateAdminAsync($"owner{i}", "correct-horse");
        }));

        Assert.Single(attempts, attempt => attempt.IsSuccess);
        Assert.All(
            attempts.Where(attempt => attempt.IsFailure),
            attempt => Assert.Equal("identity.setup_already_completed", attempt.Error.Code));
        await using var check = _provider.CreateAsyncScope();
        Assert.Single(await check.ServiceProvider.GetRequiredService<IUserProvisioning>().ListAsync());
    }

    [Fact]
    public async Task Two_administrators_demoting_each_other_at_once_leave_one_standing()
    {
        var (first, second) = await TwoAdministratorsAsync();

        var results = await Task.WhenAll(new[] { first, second }.Select(async id =>
        {
            await using var scope = _provider.CreateAsyncScope();
            return await scope.ServiceProvider.GetRequiredService<IUserProvisioning>().SetRoleAsync(id, UserRole.Member);
        }));

        Assert.Single(results, result => result.IsSuccess);
        Assert.Equal("identity.last_administrator", Assert.Single(results, result => result.IsFailure).Error.Code);
        await using var check = _provider.CreateAsyncScope();
        var accounts = await check.ServiceProvider.GetRequiredService<IUserProvisioning>().ListAsync();
        Assert.Single(accounts, account => account.IsAdministrator);
    }

    [Fact]
    public async Task Disabling_both_administrators_at_once_leaves_one_who_can_sign_in()
    {
        var (first, second) = await TwoAdministratorsAsync();

        var results = await Task.WhenAll(new[] { first, second }.Select(async id =>
        {
            await using var scope = _provider.CreateAsyncScope();
            return await scope.ServiceProvider.GetRequiredService<IUserProvisioning>().SetDisabledAsync(id, disabled: true);
        }));

        Assert.Single(results, result => result.IsSuccess);
        await using var check = _provider.CreateAsyncScope();
        var accounts = await check.ServiceProvider.GetRequiredService<IUserProvisioning>().ListAsync();
        Assert.Single(accounts, account => account.IsAdministrator && !account.IsDisabled);
    }

    [Fact]
    public async Task A_disabled_account_costs_the_same_password_check_as_a_wrong_password()
    {
        var (first, _) = await TwoAdministratorsAsync();
        await using var scope = _provider.CreateAsyncScope();
        Assert.True((await scope.ServiceProvider.GetRequiredService<IUserProvisioning>().SetDisabledAsync(first, true)).IsSuccess);
        var authenticator = scope.ServiceProvider.GetRequiredService<IAuthenticator>();
        _hasher.Verifications = 0;

        var result = await authenticator.AuthenticateAsync("first", "correct-horse");

        Assert.Equal("identity.invalid_credentials", result.Error.Code);
        Assert.Equal(1, _hasher.Verifications);
    }

    [Fact]
    public async Task A_username_longer_than_the_column_is_refused_by_name_rather_than_failing_the_insert()
    {
        await using var scope = _provider.CreateAsyncScope();
        var provisioning = scope.ServiceProvider.GetRequiredService<IUserProvisioning>();

        var created = await provisioning.CreateUserAsync(new string('a', 150), "correct-horse", UserRole.Member);

        Assert.Equal("identity.invalid_username", created.Error.Code);
    }

    [Fact]
    public async Task A_role_that_is_not_defined_is_refused()
    {
        var (first, _) = await TwoAdministratorsAsync();
        await using var scope = _provider.CreateAsyncScope();
        var provisioning = scope.ServiceProvider.GetRequiredService<IUserProvisioning>();

        var created = await provisioning.CreateUserAsync("someone", "correct-horse", (UserRole)7);
        var changed = await provisioning.SetRoleAsync(first, (UserRole)7);

        Assert.Equal("identity.invalid_role", created.Error.Code);
        Assert.Equal("identity.invalid_role", changed.Error.Code);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(1_001)]
    public async Task An_open_request_limit_outside_the_range_is_refused(int limit)
    {
        await using var scope = _provider.CreateAsyncScope();
        var provisioning = scope.ServiceProvider.GetRequiredService<IUserProvisioning>();
        var member = await provisioning.CreateUserAsync("member", "correct-horse", UserRole.Member);
        Assert.True(member.IsSuccess, member.IsSuccess ? null : member.Error.Message);

        var set = await provisioning.SetPermissionsAsync(member.Value, new UserPermissions(true, false, limit));
        var created = await provisioning.CreateUserAsync(
            "other", "correct-horse", UserRole.Member, new UserPermissions(true, false, limit));

        Assert.Equal("identity.invalid_open_request_limit", set.Error.Code);
        Assert.Equal("identity.invalid_open_request_limit", created.Error.Code);
    }

    private async Task<(UserId First, UserId Second)> TwoAdministratorsAsync()
    {
        await using var scope = _provider.CreateAsyncScope();
        var provisioning = scope.ServiceProvider.GetRequiredService<IUserProvisioning>();
        var first = await provisioning.CreateAdminAsync("first", "correct-horse");
        var second = await provisioning.CreateUserAsync("second", "correct-horse", UserRole.Administrator);
        Assert.True(first.IsSuccess && second.IsSuccess);
        return (first.Value, second.Value);
    }

    /// <summary>A cheap stand-in for Argon2id that counts how often a password is checked.</summary>
    private sealed class CountingHasher : IPasswordHasher
    {
        private int _verifications;

        public int Verifications
        {
            get => Volatile.Read(ref _verifications);
            set => Volatile.Write(ref _verifications, value);
        }

        public string Hash(string password) => "plain:" + password;

        public bool Verify(string password, string phcHash)
        {
            Interlocked.Increment(ref _verifications);
            return phcHash == "plain:" + password;
        }
    }
}
