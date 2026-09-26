using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;

namespace Cinomni.Host.Tests;

/// <summary>
/// The trust boundary the packaged image introduced: the web shell is now served from the same origin as
/// the authorized API. Routing metadata alone cannot prove what that origin answers, so this drives a real
/// server over HTTP and pins the four things that must never drift — an unknown API path is an error
/// envelope and not the shell, an unauthenticated API call is still a 401 and not the shell, a deep link
/// is the shell, and nothing outside the web root is reachable through it.
/// <para>
/// The pipeline mirrors <c>Program</c>: static files, then authentication and authorization, then the very
/// same <c>MapCinomniEndpoints</c>. Nothing connects to a database — every path exercised here is answered
/// before a handler that would need one.
/// </para>
/// </summary>
public sealed class WebClientSurfaceTests : IAsyncLifetime
{
    private const string ShellMarker = "<div id=\"root\"></div>";
    private const string OutsideTheWebRoot = "a-file-the-web-must-never-serve";

    private readonly string _contentRoot = Path.Combine(
        Path.GetTempPath(),
        $"cinomni-web-surface-{Guid.NewGuid():N}");

    private WebApplication? _app;
    private HttpClient? _client;

    public async Task InitializeAsync()
    {
        var webRoot = Path.Combine(_contentRoot, "wwwroot");
        Directory.CreateDirectory(Path.Combine(webRoot, "assets"));

        // The shape `vite build` emits: one entry document naming content-hashed files under /assets.
        await File.WriteAllTextAsync(Path.Combine(webRoot, "index.html"), $"<!doctype html>{ShellMarker}");
        await File.WriteAllTextAsync(Path.Combine(webRoot, "assets", "client-abc123.js"), "export {};");
        // A file beside the web root, standing in for anything the image holds next to it.
        await File.WriteAllTextAsync(Path.Combine(_contentRoot, "secret.txt"), OutsideTheWebRoot);

        var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions
        {
            ContentRootPath = _contentRoot,
            EnvironmentName = "Production",
        });
        builder.Services.AddCinomniModules();
        builder.WebHost.UseUrls("http://127.0.0.1:0");

        var app = builder.Build();
        app.UseStaticFiles(WebClientFiles.Options());
        app.UseAuthentication();
        app.UseAuthorization();
        app.MapCinomniEndpoints();

        await app.StartAsync();
        _app = app;
        _client = new HttpClient { BaseAddress = new Uri(app.Urls.First()) };
    }

    public async Task DisposeAsync()
    {
        _client?.Dispose();

        if (_app is not null)
        {
            await _app.StopAsync();
            await _app.DisposeAsync();
        }

        try
        {
            Directory.Delete(_contentRoot, recursive: true);
        }
        catch (IOException)
        {
            // A leftover temp directory must never fail a test run.
        }
    }

    [Fact]
    public async Task An_unknown_api_path_is_an_error_envelope_and_never_the_web_shell()
    {
        var response = await Client.GetAsync("/api/does-not-exist");
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
        Assert.Contains("api.route_not_found", body, StringComparison.Ordinal);
        Assert.DoesNotContain(ShellMarker, body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_unknown_api_path_answers_the_same_way_to_a_write_verb()
    {
        // The shell fallback only matches GET, so without the API terminator a POST would 405 and a GET
        // would return HTML. Both must be the same envelope.
        var response = await Client.PostAsync("/api/does-not-exist", content: null);
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Contains("api.route_not_found", body, StringComparison.Ordinal);
        Assert.DoesNotContain(ShellMarker, body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_authorized_api_route_still_refuses_an_anonymous_caller_instead_of_falling_back()
    {
        var response = await Client.GetAsync("/api/catalog/works");
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.DoesNotContain(ShellMarker, body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_deep_link_is_answered_with_the_shell_so_a_reload_survives()
    {
        var response = await Client.GetAsync($"/works/{Guid.NewGuid()}");
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/html", response.Content.Headers.ContentType?.MediaType);
        Assert.Contains(ShellMarker, body, StringComparison.Ordinal);
        // The shell must be revalidated, or an upgrade leaves a browser asking for assets that are gone.
        Assert.Contains(WebClientFiles.RevalidateCacheControl, CacheControl(response), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_content_hashed_asset_may_be_kept_while_the_shell_may_not()
    {
        var asset = await Client.GetAsync("/assets/client-abc123.js");
        var shell = await Client.GetAsync("/");

        Assert.Equal(HttpStatusCode.OK, asset.StatusCode);
        Assert.Contains("max-age=31536000", CacheControl(asset), StringComparison.Ordinal);
        Assert.Contains("immutable", CacheControl(asset), StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.OK, shell.StatusCode);
        Assert.Contains(WebClientFiles.RevalidateCacheControl, CacheControl(shell), StringComparison.Ordinal);
        Assert.DoesNotContain("immutable", CacheControl(shell), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("/../secret.txt")]
    [InlineData("/%2e%2e/secret.txt")]
    [InlineData("/assets/..%2fsecret.txt")]
    [InlineData("/assets/%2e%2e%2f%2e%2e%2fsecret.txt")]
    public async Task Nothing_outside_the_web_root_is_reachable_through_the_static_surface(string path)
    {
        var response = await Client.GetAsync(new Uri(path, UriKind.Relative));
        var body = await response.Content.ReadAsStringAsync();

        Assert.DoesNotContain(OutsideTheWebRoot, body, StringComparison.Ordinal);
    }

    private HttpClient Client => _client ?? throw new InvalidOperationException("The server did not start.");

    /// <summary>The header as it went over the wire, so the assertion does not depend on typed re-rendering.</summary>
    private static string CacheControl(HttpResponseMessage response) =>
        response.Headers.TryGetValues("Cache-Control", out var values)
            ? string.Join(", ", values)
            : string.Empty;
}
