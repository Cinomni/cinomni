using System.Net;
using System.Text;
using Cinomni.Host.RateLimiting;
using Cinomni.Identity;
using Cinomni.Identity.Api;
using Cinomni.Identity.Application;
using Cinomni.Identity.Contracts;
using Cinomni.Identity.Persistence;
using Cinomni.Operations;
using Cinomni.Operations.Persistence;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.DependencyInjection;

namespace Cinomni.Host.Tests;

/// <summary>
/// Boots the real anonymous Identity surface with the real rate limiter in front of it, against a real
/// PostgreSQL database. Narrow on purpose — Identity and the platform kernel, nothing else — because
/// what is under test is the middleware and the partition key, not the modules behind them.
/// <para>
/// Requests go through <see cref="TestServer.SendAsync(Action{HttpContext}, CancellationToken)"/>
/// rather than an <c>HttpClient</c>, because the whole question is which address a request is counted
/// against and that is the one thing a client cannot set.
/// </para>
/// </summary>
internal static class RateLimitHttpTestHost
{
    public static string ConnectionStringFor(string database) =>
        $"Host=localhost;Port=5442;Database={database};Username=cinomni;Password=cinomni_dev";

    public static async Task<WebApplication> StartAsync(string database, IConfiguration? configuration = null)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();

        var config = configuration ?? new ConfigurationBuilder().Build();
        builder.Services.AddOperations(ConnectionStringFor(database), configuration: config);
        builder.Services.AddIdentityModule();
        builder.Services.AddIdentityAuthentication();
        builder.Services.AddCinomniRateLimiting();

        var app = builder.Build();

        await using (var scope = app.Services.CreateAsyncScope())
        {
            var operations = scope.ServiceProvider.GetRequiredService<OperationsDbContext>();
            await operations.Database.EnsureDeletedAsync();
            await operations.Database.MigrateAsync();
            await scope.ServiceProvider.GetRequiredService<IdentityDbContext>().Database.MigrateAsync();
        }

        // The production order, including the forwarded-header middleware: whether X-Forwarded-For
        // is believed is decided by the same configuration a deployment uses, so a test can exercise
        // both sides of that trust boundary instead of only the side without a proxy.
        app.UseCinomniForwardedHeaders(config, app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Test"));
        app.UseRateLimiter();
        app.UseAuthentication();
        app.UseAuthorization();
        app.UseCinomniAccountRateLimiter();
        app.MapIdentityEndpoints();

        await app.StartAsync();
        return app;
    }

    /// <summary>
    /// One login attempt from <paramref name="clientAddress"/>, optionally claiming to be forwarding
    /// for <paramref name="forwardedFor"/> — which is the header an attacker controls.
    /// </summary>
    public static async Task<HttpContext> AttemptLoginAsync(
        this WebApplication app,
        string clientAddress,
        string username = "nobody",
        string password = "wrong-password",
        string? forwardedFor = null)
    {
        var body = Encoding.UTF8.GetBytes(
            $$"""{"username":"{{username}}","password":"{{password}}"}""");

        return await app.GetTestServer().SendAsync(context =>
        {
            if (forwardedFor is not null)
            {
                context.Request.Headers["X-Forwarded-For"] = forwardedFor;
            }

            context.Request.Method = HttpMethods.Post;
            context.Request.Path = "/api/identity/login";
            context.Request.ContentType = "application/json";
            context.Request.ContentLength = body.Length;
            context.Request.Body = new MemoryStream(body);
            // TestServer builds a context whose body-detection feature answers "no body", so minimal
            // API binding refuses the request with a bodiless 400 before the endpoint ever runs. The
            // feature is what binding asks, so the feature is what the test has to supply.
            context.Features.Set<IHttpRequestBodyDetectionFeature>(new BodyIsPresent());
            context.Connection.RemoteIpAddress = IPAddress.Parse(clientAddress);
        });
    }

    /// <summary>
    /// A member account and a session for it, made directly rather than through the anonymous routes so
    /// that building the scenario spends none of the per-address budget a test may be measuring.
    /// </summary>
    public static async Task<string> SignInNewMemberAsync(this WebApplication app, string username)
    {
        await using var scope = app.Services.CreateAsyncScope();
        var created = await scope.ServiceProvider.GetRequiredService<IUserProvisioning>()
            .CreateUserAsync(username, "correct-horse-battery", UserRole.Member);
        if (created.IsFailure)
        {
            throw new InvalidOperationException(created.Error.Message);
        }

        return (await scope.ServiceProvider.GetRequiredService<ISessionService>().IssueAsync(created.Value)).Token;
    }

    /// <summary>One authenticated call to a second-factor route, carrying <paramref name="json"/> as its body.</summary>
    public static async Task<HttpContext> PostAsAccountAsync(
        this WebApplication app,
        string path,
        string token,
        string json,
        string clientAddress = "203.0.113.50")
    {
        var body = Encoding.UTF8.GetBytes(json);

        return await app.GetTestServer().SendAsync(context =>
        {
            context.Request.Method = HttpMethods.Post;
            context.Request.Path = path;
            context.Request.Headers.Authorization = $"Bearer {token}";
            context.Request.ContentType = "application/json";
            context.Request.ContentLength = body.Length;
            context.Request.Body = new MemoryStream(body);
            context.Features.Set<IHttpRequestBodyDetectionFeature>(new BodyIsPresent());
            context.Connection.RemoteIpAddress = IPAddress.Parse(clientAddress);
        });
    }

    private sealed class BodyIsPresent : IHttpRequestBodyDetectionFeature
    {
        public bool CanHaveBody => true;
    }
}
