using System.Security.Claims;
using System.Text.Encodings.Web;
using Cinomni.Catalog;
using Cinomni.Catalog.Persistence;
using Cinomni.Identity;
using Cinomni.Identity.Api;
using Cinomni.Identity.Persistence;
using Cinomni.Monitoring;
using Cinomni.Monitoring.Api;
using Cinomni.Monitoring.Persistence;
using Cinomni.Operations;
using Cinomni.Operations.Persistence;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Cinomni.Host.Tests;

/// <summary>
/// Boots the real Monitoring HTTP surface — real routing, real <c>UseAuthentication</c>/
/// <c>UseAuthorization</c> middleware, the real Identity bearer scheme — against a real PostgreSQL
/// database, exactly the layer <c>ApiAuthorizationTests</c> cannot see (it only reads routing metadata,
/// never dispatches a request) and the Monitoring integration tests cannot see either (they resolve
/// <c>MonitoringBrowse</c> straight from DI, so they would stay green even if the endpoint stopped binding
/// it). Only a real request through the mapped route exercises the wiring the vulnerability lived in.
/// </summary>
internal static class MonitoringHttpTestHost
{
    /// <summary>
    /// Sent by a test that wants to authenticate as a principal the real bearer scheme could never
    /// produce: signed in, but carrying no <c>NameIdentifier</c> claim. Identity always sets one on a
    /// valid session (<see cref="OpaqueTokenAuthenticationHandler"/>), so this is the only way to reach
    /// the branch of <c>Viewer.From</c> that returns null for an authenticated caller.
    /// </summary>
    public const string BrokenPrincipalHeader = "X-Test-Broken-Principal";

    private const string BrokenPrincipalScheme = "TestBrokenPrincipal";
    private const string SchemeSelector = "TestSchemeSelector";

    public static string ConnectionStringFor(string database) =>
        $"Host=localhost;Port=5442;Database={database};Username=cinomni;Password=cinomni_dev";

    public static async Task<WebApplication> StartAsync(string database)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();

        builder.Services.AddOperations(ConnectionStringFor(database));
        builder.Services.AddIdentityModule();
        builder.Services.AddIdentityAuthentication();
        builder.Services.AddCatalogModule();
        builder.Services.AddMonitoringModule();

        // A second scheme, reached only via the marker header above, that authenticates without ever
        // setting NameIdentifier — see BrokenPrincipalHeader. AddIdentityAuthentication already made the
        // real bearer scheme the default; PostConfigure runs after every Configure delegate regardless of
        // registration order, so it reliably wins and makes the selector the effective default instead,
        // while still forwarding an ordinary request straight to the untouched bearer handler.
        builder.Services.AddAuthentication()
            .AddScheme<AuthenticationSchemeOptions, BrokenPrincipalHandler>(BrokenPrincipalScheme, configureOptions: null)
            .AddPolicyScheme(SchemeSelector, SchemeSelector, options =>
            {
                options.ForwardDefaultSelector = context =>
                    context.Request.Headers.ContainsKey(BrokenPrincipalHeader)
                        ? BrokenPrincipalScheme
                        : OpaqueTokenAuthenticationHandler.SchemeName;
            });
        builder.Services.PostConfigure<AuthenticationOptions>(options => options.DefaultScheme = SchemeSelector);

        var app = builder.Build();

        await using (var scope = app.Services.CreateAsyncScope())
        {
            var operations = scope.ServiceProvider.GetRequiredService<OperationsDbContext>();
            await operations.Database.EnsureDeletedAsync();
            await operations.Database.MigrateAsync();
            await scope.ServiceProvider.GetRequiredService<IdentityDbContext>().Database.MigrateAsync();
            await scope.ServiceProvider.GetRequiredService<CatalogDbContext>().Database.MigrateAsync();
            await scope.ServiceProvider.GetRequiredService<MonitoringDbContext>().Database.MigrateAsync();
        }

        app.UseAuthentication();
        app.UseAuthorization();
        app.MapMonitoringEndpoints();

        await app.StartAsync();
        return app;
    }

    /// <summary>Authenticates every request as signed in, but with no claim <c>Viewer.From</c> can read.</summary>
    private sealed class BrokenPrincipalHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            var identity = new ClaimsIdentity([new Claim(ClaimTypes.Name, "broken")], BrokenPrincipalScheme);
            var ticket = new AuthenticationTicket(new ClaimsPrincipal(identity), BrokenPrincipalScheme);
            return Task.FromResult(AuthenticateResult.Success(ticket));
        }
    }
}
