using Cinomni.Identity.Application;
using Cinomni.Identity.Contracts;
using Cinomni.Metadata.Contracts;
using Microsoft.Extensions.DependencyInjection;

namespace Cinomni.Identity.Tests;

/// <summary>
/// A ceiling is stored only when this installation can order it. Accepting a label the ladder does not
/// know would report a restriction that never applies.
/// </summary>
public sealed class ContentCeilingTests : IAsyncLifetime
{
    private ServiceProvider _provider = null!;

    public async Task InitializeAsync() =>
        _provider = await IdentityTestHost.CreateAsync(
            "cinomni_test_identity_ceiling",
            services => services.AddSingleton<IContentRatingRegion>(new FixedContentRatingRegion("ES")));

    public async Task DisposeAsync() => await _provider.DisposeAsync();

    [Fact]
    public async Task A_ceiling_is_stored_in_the_ladders_spelling_and_stamped_with_the_region()
    {
        var id = await CreateMemberAsync();

        await using var scope = _provider.CreateAsyncScope();
        var provisioning = scope.ServiceProvider.GetRequiredService<IUserProvisioning>();
        var set = await provisioning.SetPermissionsAsync(
            id, new UserPermissions(true, false, null, "12"));
        Assert.True(set.IsSuccess, set.IsSuccess ? null : set.Error.Message);

        var account = Assert.Single(await provisioning.ListAsync(), user => user.Id == id);
        Assert.Equal("12", account.Permissions.ContentCeiling);
        Assert.Equal("ES", account.Permissions.ContentCeilingRegion);
    }

    [Fact]
    public async Task An_unknown_certificate_is_refused_and_nothing_is_stored()
    {
        var id = await CreateMemberAsync();

        await using var scope = _provider.CreateAsyncScope();
        var provisioning = scope.ServiceProvider.GetRequiredService<IUserProvisioning>();
        var set = await provisioning.SetPermissionsAsync(
            id, new UserPermissions(true, false, null, "PG-13"));

        Assert.Equal("identity.invalid_content_ceiling", set.Error.Code);
        var account = Assert.Single(await provisioning.ListAsync(), user => user.Id == id);
        Assert.Null(account.Permissions.ContentCeiling);
    }

    [Fact]
    public async Task Clearing_the_ceiling_clears_the_region_with_it()
    {
        var id = await CreateMemberAsync();

        await using var scope = _provider.CreateAsyncScope();
        var provisioning = scope.ServiceProvider.GetRequiredService<IUserProvisioning>();
        Assert.True((await provisioning.SetPermissionsAsync(id, new UserPermissions(true, false, null, "16"))).IsSuccess);
        Assert.True((await provisioning.SetPermissionsAsync(id, new UserPermissions(true, false, null, null))).IsSuccess);

        var account = Assert.Single(await provisioning.ListAsync(), user => user.Id == id);
        Assert.Null(account.Permissions.ContentCeiling);
        Assert.Null(account.Permissions.ContentCeilingRegion);
    }

    [Fact]
    public async Task A_ceiling_is_refused_when_no_region_is_configured()
    {
        await using var unset = await IdentityTestHost.CreateAsync("cinomni_test_identity_ceiling_unset");
        await using var scope = unset.CreateAsyncScope();
        var provisioning = scope.ServiceProvider.GetRequiredService<IUserProvisioning>();
        var created = await provisioning.CreateUserAsync("member", "correct-horse", UserRole.Member, UserPermissions.Default);
        Assert.True(created.IsSuccess, created.IsSuccess ? null : created.Error.Message);

        var set = await provisioning.SetPermissionsAsync(created.Value, new UserPermissions(true, false, null, "12"));
        Assert.Equal("identity.content_ceiling_unavailable", set.Error.Code);
    }

    [Fact]
    public async Task Changing_another_permission_keeps_a_ceiling_set_under_a_region_no_longer_in_use()
    {
        // The users page sends the whole permission set when one toggle changes, the stored ceiling
        // with it. After the installation moves to a region whose scale has no "12", re-validating it
        // refused the toggle; a label both scales share would have been re-stamped and switched back on.
        var region = new SwitchableRegion { Current = "ES" };
        await using var host = await IdentityTestHost.CreateAsync(
            "cinomni_test_identity_ceiling_region_change",
            services => services.AddSingleton<IContentRatingRegion>(region));
        await using var scope = host.CreateAsyncScope();
        var provisioning = scope.ServiceProvider.GetRequiredService<IUserProvisioning>();
        var created = await provisioning.CreateUserAsync("member", "correct-horse", UserRole.Member, UserPermissions.Default);
        Assert.True(created.IsSuccess, created.IsSuccess ? null : created.Error.Message);
        Assert.True((await provisioning.SetPermissionsAsync(created.Value, new UserPermissions(true, false, null, "12"))).IsSuccess);

        region.Current = "US";
        var set = await provisioning.SetPermissionsAsync(created.Value, new UserPermissions(false, false, null, "12"));

        Assert.True(set.IsSuccess, set.IsSuccess ? null : set.Error.Message);
        var account = Assert.Single(await provisioning.ListAsync(), user => user.Id == created.Value);
        Assert.False(account.Permissions.CanRequest);
        Assert.Equal("12", account.Permissions.ContentCeiling);
        Assert.Equal("ES", account.Permissions.ContentCeilingRegion);
    }

    private sealed class SwitchableRegion : IContentRatingRegion
    {
        public string? Current { get; set; }
    }

    private async Task<UserId> CreateMemberAsync()
    {
        await using var scope = _provider.CreateAsyncScope();
        var provisioning = scope.ServiceProvider.GetRequiredService<IUserProvisioning>();
        var created = await provisioning.CreateUserAsync("member", "correct-horse", UserRole.Member, UserPermissions.Default);
        Assert.True(created.IsSuccess, created.IsSuccess ? null : created.Error.Message);
        return created.Value;
    }
}
