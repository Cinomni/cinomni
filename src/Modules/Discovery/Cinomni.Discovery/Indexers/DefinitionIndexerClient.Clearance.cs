using Cinomni.Discovery.Contracts;
using Cinomni.Discovery.Indexers.Definition;
using Cinomni.Kernel.Net;

namespace Cinomni.Discovery.Indexers;

/// <summary>
/// The login-walled site behind a browser challenge: FlareSolverr solves the challenge once, and every
/// request after that — sign-in, search, detail page — is a plain one sent through the same egress
/// proxy as the browser that solved it. A challenge met on the way renews the clearance and retries
/// exactly once; a second one fails the request rather than solve for ever.
/// </summary>
internal sealed partial class DefinitionIndexerClient
{
    /// <summary>Whether this search runs cleared: a login to keep, and FlareSolverr turned on for it.</summary>
    private bool RunsCleared(IndexerSummary indexer, DefinitionSession? declaredSession, IndexerCredential? credential) =>
        declaredSession is not null
        && credential is not null
        && indexer.Settings?.UseFlareSolverr == true
        && clearance is not null
        && httpClientFactory is not null
        && indexer.Id.Value != Guid.Empty;

    /// <summary>The session, signed in behind the clearance; a challenge on the way renews it once.</summary>
    private async Task<IReadOnlyList<JarCookie>?> EnsureClearedSessionAsync(
        IndexerSummary indexer,
        Uri baseUri,
        DefinitionSession declaredSession,
        IndexerCredential credential,
        CancellationToken cancellationToken)
    {
        // Solved at the login page itself: a site may serve its front page freely and challenge only
        // what is behind it, and a solve that met no challenge brings back no clearance.
        var solveAt = DefinitionQueryBuilder.TryResolve(baseUri, declaredSession.Login.UrlTemplate, out var loginUrl)
            && DefinitionQueryBuilder.SameOrigin(baseUri, loginUrl)
                ? loginUrl
                : baseUri;
        var current = await clearance!.GetAsync(indexer.Id.Value, solveAt, cancellationToken);
        try
        {
            return await sessionManager!.EnsureSessionAsync(
                indexer.Id.Value, indexer.Name, baseUri, declaredSession, credential, current, cancellationToken);
        }
        catch (IndexerChallengeException)
        {
            clearance.Invalidate(indexer.Id.Value, current);
            var renewed = await clearance.GetAsync(indexer.Id.Value, solveAt, cancellationToken);
            return await sessionManager!.EnsureSessionAsync(
                indexer.Id.Value, indexer.Name, baseUri, declaredSession, credential, renewed, cancellationToken);
        }
    }

    /// <summary>
    /// One request as the browser that solved the challenge, carrying <paramref name="session"/> next
    /// to the clearance. Answers rather than throws, like the uncleared session transport, so the caller
    /// still decides what a logged-out answer means.
    /// </summary>
    private async Task<SessionedResponse> SendClearedAsync(
        IndexerSummary indexer,
        Uri baseUri,
        DefinitionSearchQuery query,
        IReadOnlyList<JarCookie>? session,
        CancellationToken cancellationToken)
    {
        // The egress proxy refuses private destinations on its own; this keeps the refusal — and the
        // same-origin rule every definition request lives under — on this side of the wire too.
        if (!SsrfGuard.TryValidatePublicUrl(query.Url.ToString(), out _)
            || !DefinitionQueryBuilder.SameOrigin(baseUri, query.Url))
        {
            throw new HttpRequestException("Cleared request target was rejected.");
        }

        var current = await clearance!.GetAsync(indexer.Id.Value, query.Url, cancellationToken);
        var (response, challenged) = await SendOnceClearedAsync(query, session, current, cancellationToken);
        if (!challenged)
        {
            return response;
        }

        clearance.Invalidate(indexer.Id.Value, current);
        var renewed = await clearance.GetAsync(indexer.Id.Value, query.Url, cancellationToken);
        (response, challenged) = await SendOnceClearedAsync(query, session, renewed, cancellationToken);
        if (challenged)
        {
            throw new IndexerChallengeException($"Indexer {indexer.Name} kept answering with a browser challenge.");
        }

        return response;
    }

    private async Task<(SessionedResponse Response, bool Challenged)> SendOnceClearedAsync(
        DefinitionSearchQuery query,
        IReadOnlyList<JarCookie>? session,
        BrowserClearance current,
        CancellationToken cancellationToken)
    {
        // Resolved per request, like the sign-in's client, so the proxied handler rotates with the pool.
        var http = httpClientFactory!.CreateClient(IndexerClearanceCache.HttpClientName);
        using var request = new HttpRequestMessage(ToHttpMethod(query.Method), query.Url);
        BrowserChallenge.Apply(request, current, session);

        using var response = await http.SendAsync(request, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        return (new SessionedResponse(response.StatusCode, body, response.Headers.Location),
            BrowserChallenge.IsChallenge(response, body));
    }
}
