using System.Net;
using System.Net.Http.Headers;
using System.Text;

namespace Cinomni.Discovery.Tests;

/// <summary>
/// Answers every request with one canned response and records the request URIs it received, so a
/// test can assert both on what an adapter parsed and on the exact request it issued (or that it
/// issued none at all).
/// </summary>
internal sealed class FakeHttpMessageHandler(HttpStatusCode status, string body, string contentType = "text/html")
    : HttpMessageHandler
{
    public List<Uri> Requests { get; } = [];

    /// <summary>The <c>Authorization</c> header of each request, in order; null where none was sent.</summary>
    public List<AuthenticationHeaderValue?> Authorizations { get; } = [];

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Requests.Add(request.RequestUri!);
        Authorizations.Add(request.Headers.Authorization);
        return Task.FromResult(new HttpResponseMessage(status)
        {
            Content = new StringContent(body, Encoding.UTF8, contentType),
        });
    }
}
