using System.Net;
using Cinomni.Discovery.Contracts;
using Cinomni.Discovery.Application;
using Cinomni.Discovery.Diagnostics;
using Cinomni.Discovery.Indexers.Definition;
using Cinomni.Search.Contracts;
using Microsoft.Extensions.Logging;

namespace Cinomni.Discovery.Indexers;

/// <summary>
/// Production <see cref="IIndexerClient"/> for a <see cref="IndexerProtocol.Definition"/> indexer:
/// parses the definition the caller resolved for it, composes the request with
/// <see cref="DefinitionQueryBuilder"/>, issues it over the same SSRF-hardened transport
/// <see cref="TorznabIndexerClient"/> uses, and parses the response with
/// <see cref="DefinitionResponseParser"/>.
/// <para>
/// It deliberately holds no <c>DiscoveryDbContext</c>. It used to look its own definition up, which
/// put a database round trip inside <c>ReleaseSearch</c>'s parallel fan-out: two definition-backed
/// indexers then raced on the one scoped context and its one connection, and the loser was swallowed
/// into "this indexer returned nothing". The lookup now happens once, before the fan-out.
/// </para>
/// <para>
/// A definition that declares a <c>session.login</c> block signs in first — through
/// <see cref="IIndexerSessionManager"/>, with the credential the caller resolved — and every request
/// it issues carries the session's cookies. When a search response says the site expired the session
/// (status 401/403, or the definition's logged-out check selector matching), the session is dropped
/// and the sign-in runs once more before the search is retried exactly once; if even that comes back
/// expired, the search fails rather than query a private tracker unauthenticated forever. No session
/// block or an unreadable credential leaves the search exactly as an unauthenticated one.
/// </para>
/// <para>
/// A login with FlareSolverr turned on runs cleared (see the <c>.Clearance</c> part): the browser only
/// solves the site's challenge, and the session travels on plain requests carrying that clearance.
/// </para>
/// </summary>
internal sealed partial class DefinitionIndexerClient(
    HttpClient httpClient,
    ILogger<DefinitionIndexerClient>? logger = null,
    IIndexerBrowserTransport? browserTransport = null,
    IIndexerQuota? quota = null,
    IIndexerSessionManager? sessionManager = null,
    IIndexerClearance? clearance = null,
    IHttpClientFactory? httpClientFactory = null) : IIndexerClient
{
    /// <param name="credential">
    /// The indexer's stored credential, or null when it has none or it cannot be read. A definition
    /// with a session block spends it here — signing in and keeping the cookies — and one without
    /// still never uses it, because a Torznab/Newznab key travels in the query string the definition
    /// builder composes.
    /// </param>
    public async Task<IReadOnlyList<ReleaseCandidate>> SearchAsync(
        IndexerSummary indexer,
        IndexerCredential? credential,
        string? definitionContent,
        SearchCriterion criterion,
        CancellationToken cancellationToken = default)
    {
        // A misconfigured indexer (no definition, or one that was since deleted) is a broken
        // configuration, not a genuine "nothing available" — but ReleaseSearch.SearchOneAsync
        // already swallows every adapter exception into a zero-candidate result and logs it, so
        // throwing here gets the same degrade-gracefully behaviour every other adapter failure does.
        if (indexer.DefinitionId is null)
        {
            throw new InvalidOperationException($"Indexer '{indexer.Name}' has no definition to search.");
        }

        if (definitionContent is null)
        {
            throw new InvalidOperationException($"Indexer '{indexer.Name}' references a definition that no longer exists.");
        }

        var parsed = IndexerDefinitionParser.Parse(definitionContent);
        if (parsed.IsFailure)
        {
            throw new InvalidOperationException(
                $"Indexer '{indexer.Name}' has an invalid definition ({parsed.Error.Code}).");
        }

        var baseUri = new Uri(indexer.BaseUrl, UriKind.Absolute);
        var query = DefinitionQueryBuilder.Build(parsed.Value, criterion, baseUri);
        if (query.IsFailure)
        {
            // No request template for this content kind — the same shape a Torznab endpoint that
            // rejects a mode produces: no results, not a failure.
            logger?.LogWarning(
                "Indexer '{IndexerName}' skipped a definition query ({FailureCode}).",
                indexer.Name,
                query.Error.Code);
            return [];
        }

        if (indexer.Id.Value != Guid.Empty && quota is not null && !await quota.TryConsumeAsync(
                indexer.Id.Value, IndexerRequestKind.Query, indexer.Settings?.QueryLimit, cancellationToken))
        {
            throw new IndexerQuotaExceededException(IndexerRequestKind.Query);
        }

        // A session block plus a readable credential is what turns the declared login into a kept one.
        // Everything else searches exactly as it did before sessions existed — including a session
        // block whose credential is unreadable, which is the same "no usable secret" situation as
        // having none. With FlareSolverr on, the kept session runs cleared.
        var declaredSession = parsed.Value.Session;
        var cleared = RunsCleared(indexer, declaredSession, credential);
        var sessionCredential = declaredSession is not null
            && credential is not null
            && (indexer.Settings?.UseFlareSolverr != true || cleared)
                ? credential
                : null;
        IReadOnlyList<JarCookie>? session = null;
        if (declaredSession is not null && credential is not null && indexer.Settings?.UseFlareSolverr == true && !cleared)
        {
            // Only reachable without the clearance services wired (a test host, a trimmed composition):
            // never silently private without saying why.
            logger?.LogWarning(
                "Indexer '{IndexerName}' declares a login and uses FlareSolverr, but no clearance transport is "
                + "available; it is being searched unauthenticated.",
                indexer.Name);
        }

        if (sessionCredential is not null)
        {
            if (sessionManager is null)
            {
                throw new InvalidOperationException("Indexer session support is unavailable.");
            }

            // Sign-in requests are the session's own and deliberately consume no query or grab
            // quota: the quota counts what the site's release listings cost, and the manager's
            // per-indexer gate means concurrent searches share one sign-in.
            session = cleared
                ? await EnsureClearedSessionAsync(indexer, baseUri, declaredSession!, sessionCredential, cancellationToken)
                : await sessionManager.EnsureSessionAsync(
                    indexer.Id.Value, indexer.Name, baseUri, declaredSession!, sessionCredential,
                    cancellationToken: cancellationToken);
        }

        Task<SessionedResponse> SendWithSessionAsync(IReadOnlyList<JarCookie> jar) => cleared
            ? SendClearedAsync(indexer, baseUri, query.Value, jar, cancellationToken)
            : FetchWithSessionAsync(query.Value, jar, cancellationToken);

        string body;
        if (session is not null)
        {
            var response = await SendWithSessionAsync(session);
            if (ResponseSaysLoggedOut(response, declaredSession!.Check, baseUri, declaredSession))
            {
                // The site refused the session. One that had worked has expired, and is renewed
                // here — once, by the same bound as every other retry here, and quota-free like
                // every sign-in. One that never worked was a failed sign-in: the manager counts it,
                // and its backoff turns the renewal below into a skipped search.
                await sessionManager!.ReportRejectedAsync(indexer.Id.Value, indexer.Name, session, cancellationToken);
                DiscoveryMetrics.RecordRelogin(indexer.Name);
                session = cleared
                    ? await EnsureClearedSessionAsync(indexer, baseUri, declaredSession, sessionCredential!, cancellationToken)
                    : await sessionManager.EnsureSessionAsync(
                        indexer.Id.Value, indexer.Name, baseUri, declaredSession, sessionCredential!,
                        cancellationToken: cancellationToken);
                if (session is null)
                {
                    logger?.LogWarning(
                        "Indexer '{IndexerName}' could not sign in again after its session was refused; its search was skipped.",
                        indexer.Name);
                    throw new InvalidOperationException(
                        $"Indexer '{indexer.Name}' could not sign in; its search was skipped.");
                }

                response = await SendWithSessionAsync(session);
                if (ResponseSaysLoggedOut(response, declaredSession.Check, baseUri, declaredSession))
                {
                    await sessionManager.ReportRejectedAsync(indexer.Id.Value, indexer.Name, session, cancellationToken);
                    logger?.LogWarning(
                        "Indexer '{IndexerName}' was expired again immediately after signing in; its search was skipped.",
                        indexer.Name);
                    throw new InvalidOperationException(
                        $"Indexer '{indexer.Name}' session expired again after signing in; its search was skipped.");
                }
            }

            EnsureSearchable(response, indexer.Name);
            // The one proof a sign-in was real, and the only thing that clears its failure count.
            await sessionManager!.ConfirmAsync(indexer.Id.Value, session, cancellationToken);
            body = response.Body;
        }
        else
        {
            body = await FetchAsync(indexer, baseUri, query.Value, cookieHeader: null, cancellationToken);
        }

        var extraction = DefinitionResponseParser.Parse(parsed.Value, body, indexer.Name, query.Value.Url);

        // A field rule that stopped matching degrades the release, it does not remove it: a site can
        // change its size column overnight, and dropping its releases (or failing the search) would
        // cost the household far more than an unknown size does. So the candidates are returned as
        // they are — an unreadable size reads as zero, exactly as an indexer that publishes no size —
        // and the failure is made visible here instead of vanishing. The operator reproduces it
        // against a saved page through the definition dry run, which names the rule and the value.
        if (extraction.FieldIssues.Count > 0)
        {
            logger?.LogWarning(
                "Indexer '{IndexerName}' returned {CandidateCount} releases, but {IssueCount} declared field "
                + "values could not be read ({Summary}). Those releases keep a neutral value; re-run the "
                + "definition dry run against a saved page to see which rule to fix.",
                indexer.Name,
                extraction.Candidates.Count,
                extraction.FieldIssues.Count,
                DefinitionFieldIssues.Summarize(extraction.FieldIssues));
        }

        if (parsed.Value.Search.Details is not { } details)
        {
            return extraction.Candidates;
        }

        var resolved = new List<ReleaseCandidate>();
        var exhausted = false;
        var detailFailures = 0;
        var failuresInARow = 0;
        foreach (var candidate in extraction.Candidates.Take(details.MaxRequests))
        {
            // A site whose detail pages keep failing — a browser challenge that never solves takes a
            // minute each — is not asked twenty times. The rows already resolved are kept.
            if (failuresInARow >= MaxDetailFailuresInARow)
            {
                logger?.LogWarning(
                    "Indexer '{IndexerName}' stopped resolving release detail pages after {Failures} failures in a row.",
                    indexer.Name,
                    failuresInARow);
                break;
            }

            if (candidate.DownloadUrl.StartsWith("magnet:", StringComparison.OrdinalIgnoreCase))
            {
                resolved.Add(candidate);
                continue;
            }

            if (!Uri.TryCreate(candidate.DownloadUrl, UriKind.Absolute, out var detailUri)
                || !DefinitionQueryBuilder.SameOrigin(baseUri, detailUri))
            {
                continue;
            }

            if (indexer.Id.Value != Guid.Empty && quota is not null && !await quota.TryConsumeAsync(
                    indexer.Id.Value, IndexerRequestKind.Grab, indexer.Settings?.GrabLimit, cancellationToken))
            {
                exhausted = true;
                break;
            }

            try
            {
                // Details carry the session's cookies (a private tracker serves them to members
                // only) but never renew it: expiry here drops that row via the usual failure path.
                var detailQuery = new DefinitionSearchQuery(DefinitionHttpMethod.Get, detailUri);
                var detailBody = cleared
                    ? ClearedBody(await SendClearedAsync(indexer, baseUri, detailQuery, session, cancellationToken), indexer.Name)
                    : await FetchAsync(
                        indexer, baseUri, detailQuery, IndexerSessionCookies.HeaderFor(session, detailUri), cancellationToken);
                var link = DefinitionDetailParser.Extract(detailBody, details, detailUri);
                failuresInARow = 0; // the site answered; a page it answered wrongly is not a transport failure
                if (link.IsSuccess)
                {
                    resolved.Add(candidate with
                    {
                        Guid = DefinitionResponseParser.StableGuid(link.Value),
                        DownloadUrl = link.Value,
                    });
                }
                else
                {
                    detailFailures++;
                    logger?.LogWarning(
                        "Indexer '{IndexerName}' could not extract a release detail link ({FailureCode}).",
                        indexer.Name,
                        link.Error.Code);
                }
            }
            catch (Exception failure) when (failure is not OperationCanceledException)
            {
                // A failed detail request consumes quota and only drops that row.
                detailFailures++;
                failuresInARow++;
                logger?.LogWarning(
                    "Indexer '{IndexerName}' could not resolve a release detail page ({FailureType}).",
                    indexer.Name,
                    failure.GetType().Name);
            }
        }

        if (resolved.Count == 0 && exhausted)
        {
            throw new IndexerQuotaExceededException(IndexerRequestKind.Grab);
        }

        if (resolved.Count == 0 && detailFailures > 0)
        {
            throw new InvalidOperationException(
                $"Indexer '{indexer.Name}' could not resolve any release detail pages.");
        }

        return resolved;
    }

    /// <summary>
    /// How many detail requests may fail outright, one after another, before the rest are skipped.
    /// Through the browser transport each can take its full solve allowance, so twenty in a row held a
    /// browser slot for twenty minutes and the whole search waited with it.
    /// </summary>
    internal const int MaxDetailFailuresInARow = 2;

    private async Task<string> FetchAsync(
        IndexerSummary indexer,
        Uri baseUri,
        DefinitionSearchQuery query,
        string? cookieHeader,
        CancellationToken cancellationToken)
    {
        if (indexer.Settings?.UseFlareSolverr == true)
        {
            if (browserTransport is null)
            {
                throw new InvalidOperationException("Browser transport is unavailable.");
            }

            return await browserTransport.FetchAsync(baseUri, query.Url, cancellationToken);
        }

        using var request = new HttpRequestMessage(ToHttpMethod(query.Method), query.Url);
        if (cookieHeader is not null)
        {
            request.Headers.TryAddWithoutValidation("Cookie", cookieHeader);
        }

        using var response = await httpClient.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStringAsync(cancellationToken);
    }

    /// <summary>
    /// The search request with the session attached, answering instead of throwing: the status and
    /// body come back to the caller, which is what decides whether the site has just said "logged
    /// out" — that verdict, not this transport, owns the renew-and-retry decision.
    /// </summary>
    private async Task<SessionedResponse> FetchWithSessionAsync(
        DefinitionSearchQuery query, IReadOnlyList<JarCookie> session, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(ToHttpMethod(query.Method), query.Url);
        var cookieHeader = IndexerSessionCookies.HeaderFor(session, query.Url);
        if (cookieHeader is not null)
        {
            request.Headers.TryAddWithoutValidation("Cookie", cookieHeader);
        }

        using var response = await httpClient.SendAsync(request, cancellationToken);
        return new SessionedResponse(
            response.StatusCode,
            await response.Content.ReadAsStringAsync(cancellationToken),
            response.Headers.Location);
    }

    private readonly record struct SessionedResponse(HttpStatusCode Status, string Body, Uri? Location)
    {
        public bool IsSuccess => (int)Status is >= 200 and < 300;
    }

    /// <summary>
    /// Turns anything that is not a 2xx into a failure, which is what the unauthenticated path has
    /// always done through <c>EnsureSuccessStatusCode</c>. Skipping it would hand a 500, a 503 or an
    /// unrecognised redirect straight to the row extractor, which finds no rows and reports a
    /// perfectly healthy search that returned nothing — the one answer this module must never give,
    /// because downstream cannot tell it from a title genuinely not being on the site.
    /// </summary>
    private static void EnsureSearchable(SessionedResponse response, string indexerName)
    {
        if (!response.IsSuccess)
        {
            throw new HttpRequestException(
                $"Indexer {indexerName} answered {(int)response.Status} to its search.", null, response.Status);
        }
    }

    /// <summary>
    /// Expiry is a status the site reserves for signed-out callers, or — when the definition declares
    /// one — its own logged-out marker in the body of a response that would otherwise read as a
    /// normal empty search. A 200 with the login form on it must not count as "no results".
    /// </summary>
    private static bool ResponseSaysLoggedOut(
        SessionedResponse response, DefinitionSessionCheck? check, Uri baseUri, DefinitionSession session) =>
        response.Status is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden
        || RedirectsToLogin(response, baseUri, session)
        || (check is { } declared && DefinitionSessionMatcher.IsLoggedOut(response.Body, declared.Selector));

    /// <summary>
    /// The most common way a site says the session is gone: a redirect back to its login page. The
    /// transport never follows redirects, so such a response arrives with an empty body — no status
    /// the check knows, and nothing for the check selector to match. Without this it would read as a
    /// search that found nothing.
    /// </summary>
    private static bool RedirectsToLogin(SessionedResponse response, Uri baseUri, DefinitionSession session)
    {
        if ((int)response.Status is not (>= 300 and < 400) || response.Location is null)
        {
            return false;
        }

        return DefinitionQueryBuilder.TryResolve(baseUri, session.Login.UrlTemplate, out var loginUrl)
            && DefinitionQueryBuilder.TryResolve(baseUri, response.Location.OriginalString, out var target)
            && string.Equals(target.AbsolutePath, loginUrl.AbsolutePath, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>A cleared detail page's body, failing on anything but a 2xx like the plain transport does.</summary>
    private static string ClearedBody(SessionedResponse response, string indexerName)
    {
        EnsureSearchable(response, indexerName);
        return response.Body;
    }

    private static HttpMethod ToHttpMethod(DefinitionHttpMethod method) =>
        method == DefinitionHttpMethod.Post ? HttpMethod.Post : HttpMethod.Get;
}
