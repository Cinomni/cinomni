using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Cinomni.Identity;
using Cinomni.Identity.Api;
using Cinomni.Identity.Application;
using Cinomni.Identity.Persistence;
using Cinomni.Operations;
using Cinomni.Operations.Persistence;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Cinomni.Host.Tests;

/// <summary>
/// What the login route answers an account that keeps a second factor, read over HTTP — the shape the
/// web client counts its countdown from.
/// </summary>
[Trait("Category", "RequiresDatabase")]
public sealed class IdentityLoginHttpTests
{
    private const string Database = "cinomni_test_host_identity_login";

    [Fact]
    public async Task A_challenge_carries_its_lifetime_as_a_duration_as_well_as_an_instant()
    {
        await using var app = await StartAsync();
        await using (var scope = app.Services.CreateAsyncScope())
        {
            var admin = await scope.ServiceProvider.GetRequiredService<IUserProvisioning>()
                .CreateAdminAsync("owner", "correct-horse");
            Assert.True(admin.IsSuccess, admin.IsSuccess ? null : admin.Error.Message);

            // The login route only asks whether the factor is confirmed; the secret itself is never
            // read until a code is redeemed, which this test does not do.
            await scope.ServiceProvider.GetRequiredService<IdentityDbContext>().Users
                .Where(u => u.Id == admin.Value.Value)
                .ExecuteUpdateAsync(set => set.SetProperty(u => u.TotpConfirmedAt, DateTimeOffset.UtcNow));
        }

        var response = await app.GetTestClient().PostAsJsonAsync(
            "/api/identity/login", new { username = "owner", password = "correct-horse" });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.True(body.RootElement.GetProperty("twoFactorRequired").GetBoolean());
        Assert.Equal(
            (int)LoginChallenge.Lifetime.TotalSeconds,
            body.RootElement.GetProperty("expiresInSeconds").GetInt32());
        Assert.True(body.RootElement.TryGetProperty("expiresAt", out _));
    }

    private static async Task<WebApplication> StartAsync()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();

        builder.Services.AddOperations(
            $"Host=localhost;Port=5442;Database={Database};Username=cinomni;Password=cinomni_dev");
        builder.Services.AddIdentityModule();
        builder.Services.AddIdentityAuthentication();
        builder.Services.AddCinomniApiErrors();

        var app = builder.Build();
        await using (var scope = app.Services.CreateAsyncScope())
        {
            var operations = scope.ServiceProvider.GetRequiredService<OperationsDbContext>();
            await operations.Database.EnsureDeletedAsync();
            await operations.Database.MigrateAsync();
            await scope.ServiceProvider.GetRequiredService<IdentityDbContext>().Database.MigrateAsync();
        }

        app.UseCinomniApiErrors();
        app.UseAuthentication();
        app.UseAuthorization();
        app.MapIdentityEndpoints();
        await app.StartAsync();
        return app;
    }
}
