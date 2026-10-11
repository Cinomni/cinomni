using System.Net;
using System.Net.Http.Headers;
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
/// Changing your own password, read over HTTP: the statuses and envelope the web client tells its
/// messages apart by, and the session it is still holding afterwards.
/// </summary>
[Trait("Category", "RequiresDatabase")]
public sealed class IdentityPasswordHttpTests
{
    private const string Database = "cinomni_test_host_identity_password";

    [Fact]
    public async Task A_change_answers_no_content_and_keeps_only_the_calling_session()
    {
        await using var app = await StartAsync();
        var client = app.GetTestClient();
        var kept = await SignInAsync(client);
        var other = await SignInAsync(client);

        var response = await PostAsync(client, kept, "correct-horse", "battery-staple");

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await GetMeAsync(client, kept)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await GetMeAsync(client, other)).StatusCode);
    }

    [Fact]
    public async Task A_wrong_current_password_is_a_401_in_the_envelope_and_does_not_end_the_session()
    {
        await using var app = await StartAsync();
        var client = app.GetTestClient();
        var token = await SignInAsync(client);

        var response = await PostAsync(client, token, "not-the-password", "battery-staple");

        // The envelope is what tells the web client this 401 is about the field, not an expired session.
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal("identity.invalid_credentials", await ErrorCodeAsync(response));
        Assert.Equal(HttpStatusCode.OK, (await GetMeAsync(client, token)).StatusCode);
    }

    [Fact]
    public async Task The_same_password_again_is_a_400()
    {
        await using var app = await StartAsync();
        var client = app.GetTestClient();
        var token = await SignInAsync(client);

        var response = await PostAsync(client, token, "correct-horse", "correct-horse");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("identity.password_unchanged", await ErrorCodeAsync(response));
    }

    [Fact]
    public async Task Without_a_session_the_route_is_closed()
    {
        await using var app = await StartAsync();

        var response = await app.GetTestClient().PostAsJsonAsync(
            "/api/identity/password", new { currentPassword = "correct-horse", newPassword = "battery-staple" });

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    private static async Task<string> SignInAsync(HttpClient client)
    {
        var response = await client.PostAsJsonAsync(
            "/api/identity/login", new { username = "owner", password = "correct-horse" });
        response.EnsureSuccessStatusCode();
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return body.RootElement.GetProperty("token").GetString()!;
    }

    private static Task<HttpResponseMessage> PostAsync(HttpClient client, string token, string current, string next)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/identity/password")
        {
            Content = JsonContent.Create(new { currentPassword = current, newPassword = next }),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client.SendAsync(request);
    }

    private static Task<HttpResponseMessage> GetMeAsync(HttpClient client, string token)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, "/api/identity/me");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client.SendAsync(request);
    }

    private static async Task<string?> ErrorCodeAsync(HttpResponseMessage response)
    {
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return body.RootElement.GetProperty("error").GetString();
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

            var admin = await scope.ServiceProvider.GetRequiredService<IUserProvisioning>()
                .CreateAdminAsync("owner", "correct-horse");
            Assert.True(admin.IsSuccess, admin.IsSuccess ? null : admin.Error.Message);
        }

        app.UseCinomniApiErrors();
        app.UseAuthentication();
        app.UseAuthorization();
        app.MapIdentityEndpoints();
        await app.StartAsync();
        return app;
    }
}
