using System.Net.Http.Headers;
using System.Text.Json;
using Cinomni.Identity.Application;
using Cinomni.Identity.Contracts;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace Cinomni.Host.Tests;

/// <summary>
/// What <c>GET /api/identity/me</c> tells an account about itself. The web client binds to this
/// shape, and a field the projection forgets is invisible from every unit test behind it — which is
/// exactly how the indexer listing once reported healthy indexers it could not search.
/// </summary>
[Trait("Category", "RequiresDatabase")]
public sealed class IdentityMeHttpTests
{
    private const string Database = "cinomni_test_host_identity_me";

    [Theory]
    // The three states an account can be in, all of which it has to be able to see. Discovering your
    // own limit by hitting it is not an interface.
    [InlineData(3, "3")]
    [InlineData(0, "0")]
    [InlineData(null, "null")]
    public async Task Me_reports_the_accounts_own_request_limit(int? limit, string expected)
    {
        // The host maps the whole Identity surface; it is named for the rate limiter because that is
        // what it was built for, and the pipeline it composes is the production one either way.
        await using var app = await RateLimitHttpTestHost.StartAsync($"{Database}_{expected}");

        string token;
        await using (var scope = app.Services.CreateAsyncScope())
        {
            var provisioning = scope.ServiceProvider.GetRequiredService<IUserProvisioning>();
            var admin = await provisioning.CreateAdminAsync("owner", "correct-horse");
            Assert.True(admin.IsSuccess, admin.IsSuccess ? null : admin.Error.Message);

            var member = await provisioning.CreateUserAsync(
                "member",
                "correct-horse",
                UserRole.Member,
                new UserPermissions(CanRequest: true, RequestsAutoApproved: false, OpenRequestLimit: limit));
            Assert.True(member.IsSuccess, member.IsSuccess ? null : member.Error.Message);

            token = (await scope.ServiceProvider.GetRequiredService<ISessionService>()
                .IssueAsync(member.Value)).Token;
        }

        var client = app.GetTestClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        var body = await client.GetStringAsync("/api/identity/me");

        using var document = JsonDocument.Parse(body);
        var permissions = document.RootElement.GetProperty("permissions");
        Assert.Equal(expected, permissions.GetProperty("openRequestLimit").GetRawText());
    }

    [Fact]
    public async Task An_administrator_reports_no_cap_of_their_own()
    {
        await using var app = await RateLimitHttpTestHost.StartAsync($"{Database}_admin");

        string token;
        await using (var scope = app.Services.CreateAsyncScope())
        {
            var provisioning = scope.ServiceProvider.GetRequiredService<IUserProvisioning>();
            var admin = await provisioning.CreateAdminAsync("owner", "correct-horse");
            Assert.True(admin.IsSuccess, admin.IsSuccess ? null : admin.Error.Message);
            token = (await scope.ServiceProvider.GetRequiredService<ISessionService>()
                .IssueAsync(admin.Value)).Token;
        }

        var client = app.GetTestClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        var body = await client.GetStringAsync("/api/identity/me");

        // Zero, not null: an administrator is unconstrained everywhere else and their requests are
        // approved on submission, so they never occupy the decision queue this cap exists to protect.
        using var document = JsonDocument.Parse(body);
        Assert.Equal(
            "0", document.RootElement.GetProperty("permissions").GetProperty("openRequestLimit").GetRawText());
    }
}
