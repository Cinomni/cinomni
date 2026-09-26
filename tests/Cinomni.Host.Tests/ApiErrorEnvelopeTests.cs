using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Cinomni.Identity;
using Cinomni.Identity.Api;
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
/// A body the API cannot use answers in the <c>{ error, message }</c> envelope with a 4xx, never as a
/// bare 500. A login body missing its password used to reach the authenticator as a null string.
/// </summary>
[Trait("Category", "RequiresDatabase")]
public sealed class ApiErrorEnvelopeTests
{
    private const string Database = "cinomni_test_host_api_errors";

    [Theory]
    [InlineData("""{ "username": "owner" }""")]
    [InlineData("""{ "password": "correct-horse" }""")]
    [InlineData("""{}""")]
    [InlineData("""not json at all""")]
    public async Task A_login_body_that_cannot_be_used_is_a_400_in_the_envelope(string body)
    {
        await using var app = await StartAsync();
        var client = app.GetTestClient();

        var response = await client.PostAsync(
            "/api/identity/login", new StringContent(body, Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        using var envelope = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(ApiErrorHandling.InvalidRequest, envelope.RootElement.GetProperty("error").GetString());
        // Fixed text: nothing of the parser's own message, which names types and positions.
        Assert.DoesNotContain("Cinomni", envelope.RootElement.GetProperty("message").GetString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("/api/identity/login", """{ "username": "owner", "password": null }""")]
    [InlineData("/api/identity/login/two-factor", """{ "challenge": "x", "code": null }""")]
    public async Task A_required_field_sent_as_null_is_a_400_rather_than_a_null_reaching_the_service(string path, string body)
    {
        // Present satisfies [JsonRequired]; null did not stop it binding into a non-nullable string.
        await using var app = await StartAsync();
        var client = app.GetTestClient();

        var response = await client.PostAsync(path, new StringContent(body, Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        using var envelope = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("identity.invalid_request", envelope.RootElement.GetProperty("error").GetString());
    }

    [Fact]
    public async Task A_role_number_that_names_no_role_is_refused()
    {
        await using var app = await StartAsync();
        var client = await RequestsHttpTests.SignedInClientAsync(app);

        var response = await client.PostAsJsonAsync(
            "/api/identity/users", new { username = "someone", password = "correct-horse", role = 7 });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        using var envelope = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("identity.invalid_role", envelope.RootElement.GetProperty("error").GetString());
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
