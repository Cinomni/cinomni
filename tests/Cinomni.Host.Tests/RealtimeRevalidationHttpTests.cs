using Cinomni.Identity.Application;
using Cinomni.Identity.Contracts;
using Cinomni.Kernel.Security;
using Cinomni.RealTime.Api;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace Cinomni.Host.Tests;

/// <summary>
/// The live stream's periodic check, run against the production sign-in: the real token handler, the real
/// session table. The RealTime unit tests prove the mechanism with a scripted scheme; this proves it sees
/// what Identity actually does to a session — a revocation, a disabled account, a demotion.
/// </summary>
[Trait("Category", "RequiresDatabase")]
public sealed class RealtimeRevalidationHttpTests
{
    private const string Database = "cinomni_test_host_realtime_revalidation";

    [Fact]
    public async Task An_open_stream_stops_being_entitled_when_its_session_is_revoked()
    {
        await using var app = await RateLimitHttpTestHost.StartAsync($"{Database}_revoked");
        var (token, _) = await SignInAdministratorAsync(app.Services);
        var context = RequestWith(app.Services, token);
        var reader = new Viewer(await UserIdOfAsync(app.Services, token), IsAdministrator: true);

        Assert.True(await RealtimeEndpoints.StillEntitledAsync(context, reader));

        await using (var scope = app.Services.CreateAsyncScope())
        {
            Assert.True(await scope.ServiceProvider.GetRequiredService<ISessionService>().RevokeAsync(token));
        }

        Assert.False(await RealtimeEndpoints.StillEntitledAsync(context, reader));
    }

    [Fact]
    public async Task An_open_stream_stops_being_entitled_when_its_reader_is_demoted()
    {
        await using var app = await RateLimitHttpTestHost.StartAsync($"{Database}_demoted");
        var (token, userId) = await SignInAdministratorAsync(app.Services, alsoCreateAnotherAdmin: true);
        var context = RequestWith(app.Services, token);
        var reader = new Viewer(userId.Value, IsAdministrator: true);
        Assert.True(await RealtimeEndpoints.StillEntitledAsync(context, reader));

        await using (var scope = app.Services.CreateAsyncScope())
        {
            var demoted = await scope.ServiceProvider.GetRequiredService<IUserProvisioning>()
                .SetRoleAsync(userId, UserRole.Member);
            Assert.True(demoted.IsSuccess, demoted.IsSuccess ? null : demoted.Error.Message);
        }

        Assert.False(await RealtimeEndpoints.StillEntitledAsync(context, reader));
    }

    private static async Task<(string Token, UserId UserId)> SignInAdministratorAsync(
        IServiceProvider services, bool alsoCreateAnotherAdmin = false)
    {
        await using var scope = services.CreateAsyncScope();
        var provisioning = scope.ServiceProvider.GetRequiredService<IUserProvisioning>();
        var admin = await provisioning.CreateAdminAsync("owner", "correct-horse");
        Assert.True(admin.IsSuccess, admin.IsSuccess ? null : admin.Error.Message);
        if (alsoCreateAnotherAdmin)
        {
            // The last administrator cannot be demoted; a second one makes the demotion legal.
            var other = await provisioning.CreateUserAsync(
                "deputy", "correct-horse", UserRole.Administrator,
                new UserPermissions(CanRequest: true, RequestsAutoApproved: true));
            Assert.True(other.IsSuccess, other.IsSuccess ? null : other.Error.Message);
        }

        var token = (await scope.ServiceProvider.GetRequiredService<ISessionService>().IssueAsync(admin.Value)).Token;
        return (token, admin.Value);
    }

    private static async Task<Guid> UserIdOfAsync(IServiceProvider services, string token)
    {
        await using var scope = services.CreateAsyncScope();
        var session = await scope.ServiceProvider.GetRequiredService<ISessionService>().ValidateAsync(token);
        return session.Value.Id.Value;
    }

    /// <summary>The stream's request as the server holds it: the header it arrived with, and the app's services.</summary>
    private static DefaultHttpContext RequestWith(IServiceProvider services, string token)
    {
        var context = new DefaultHttpContext { RequestServices = services };
        context.Request.Method = HttpMethods.Get;
        context.Request.Path = "/api/realtime/stream";
        context.Request.Headers.Authorization = $"Bearer {token}";
        return context;
    }
}
