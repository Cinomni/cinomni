using System.Net;
using System.Text;

namespace Cinomni.Metadata.Tests;

/// <summary>
/// Scripts an <see cref="HttpClient"/> for the provider adapters: routes are matched, in registration
/// order, by a substring of the request's path and query, and every request is recorded so a test can
/// assert on the calls an adapter actually made (pagination, per-season fan-out, the season type it
/// asked for). An unmatched request answers <c>404</c> rather than throwing, so a missing route shows up
/// as a parsing assertion rather than an opaque connection error.
/// <para>
/// This is the module's only HTTP double. The three REST adapters had no coverage at all before the
/// series work, which was tolerable for a couple of hundred lines of movie parsing and is not for
/// seasons, specials, absolute numbering, pagination and embeds.
/// </para>
/// </summary>
internal sealed class FakeHttpMessageHandler : HttpMessageHandler
{
    private readonly List<(string Match, HttpStatusCode Status, string Body)> _routes = [];
    private readonly List<(string Match, HttpStatusCode Status)> _once = [];

    /// <summary>Every request path+query the adapter issued, in order.</summary>
    public List<string> Requests { get; } = [];

    /// <summary>Answers any request whose path and query contains <paramref name="match"/> with JSON.</summary>
    public FakeHttpMessageHandler Respond(string match, string json)
    {
        _routes.Add((match, HttpStatusCode.OK, json));
        return this;
    }

    /// <summary>Answers a matching request with a bare status code (no body).</summary>
    public FakeHttpMessageHandler RespondWithStatus(string match, HttpStatusCode status)
    {
        _routes.Add((match, status, string.Empty));
        return this;
    }

    /// <summary>
    /// Answers the next matching request — only that one — with a bare status, ahead of every route:
    /// how a provider rejects a token once and then accepts a fresh one.
    /// </summary>
    public FakeHttpMessageHandler RespondOnceWithStatus(string match, HttpStatusCode status)
    {
        _once.Add((match, status));
        return this;
    }

    /// <summary>How many recorded requests contain <paramref name="match"/>.</summary>
    public int CountOf(string match) => Requests.Count(request => request.Contains(match, StringComparison.Ordinal));

    /// <summary>A client bound to this handler with the given (synthetic) base address.</summary>
    public HttpClient CreateClient(string baseAddress) => new(this, disposeHandler: false)
    {
        BaseAddress = new Uri(baseAddress),
    };

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var pathAndQuery = request.RequestUri?.PathAndQuery ?? string.Empty;
        Requests.Add(pathAndQuery);

        var once = _once.FindIndex(route => pathAndQuery.Contains(route.Match, StringComparison.Ordinal));
        if (once >= 0)
        {
            var status = _once[once].Status;
            _once.RemoveAt(once);
            return Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(string.Empty) });
        }

        foreach (var (match, status, body) in _routes)
        {
            if (!pathAndQuery.Contains(match, StringComparison.Ordinal))
            {
                continue;
            }

            return Task.FromResult(new HttpResponseMessage(status)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            });
        }

        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound)
        {
            Content = new StringContent("{}", Encoding.UTF8, "application/json"),
        });
    }
}
