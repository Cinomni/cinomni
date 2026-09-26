using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using AngleSharp.Html.Parser;
using Cinomni.Discovery.Application;
using Cinomni.Discovery.Diagnostics;
using Cinomni.Discovery.Persistence;
using Cinomni.Kernel.Diagnostics;
using Cinomni.Operations.Settings;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Cinomni.Discovery.Indexers.Definition;

/// <summary>
/// Executes and keeps the login session a definition's <c>session.login</c> block declares: signs in
/// with the indexer's stored credential when no session is kept, holds the cookies the sequence
/// captured (encrypted at rest, keyed to the indexer), and forgets everything on demand so the next
/// search signs in again.
/// <para>
/// One sign-in runs at a time per indexer: a <see cref="SemaphoreSlim"/> gate per id (the pattern
/// <see cref="FlareSolverrClient"/>'s browser slots use) makes concurrent searches share the first
/// one's session instead of double-logging-in against a tracker that counts attempts. At most one
/// login attempt happens per <see cref="IIndexerSessionManager.EnsureSessionAsync"/> call — renewal
/// after expiry is the caller's decision, which is what keeps "retry exactly once" the search path's
/// own bound.
/// </para>
/// <para>
/// A kept session is remembered against a hash of the credential it signed in with, so replacing or
/// clearing the credential invalidates it by construction: the next search sees a different
/// fingerprint, finds no stored session (the administration deleted the row with the credential), and
/// signs in again. The manager needs no hook into the administration to make that true.
/// </para>
/// <para>
/// The manager owns its scopes for persistence on purpose: it runs inside <c>ReleaseSearch</c>'s
/// parallel fan-out, where the module's scoped <c>DiscoveryDbContext</c> must not be touched. Each
/// write opens its own short scope (and its own connection), outside any caller's transaction.
/// </para>
/// </summary>
/// <summary>
/// Dropping one indexer's kept session, and nothing else. It is public because the administration
/// surface — which replaces and clears credentials — has to reach it, while the manager's own
/// interface stays internal: what it hands back is a cookie jar, and a jar has no business on a
/// public signature.
/// </summary>
public interface IIndexerSessionInvalidator
{
    /// <summary>
    /// Forgets the cached session and deletes the stored one, attempt history included: the
    /// credential it belonged to was replaced or cleared, so the next sign-in starts from nothing.
    /// A session the site refused goes through <c>ReportRejectedAsync</c> instead, which keeps it.
    /// </summary>
    Task InvalidateAsync(Guid indexerId, CancellationToken cancellationToken = default);
}

internal interface IIndexerSessionManager : IIndexerSessionInvalidator
{
    /// <summary>
    /// The session's cookies for this indexer, signing in first if none are kept. Null means the
    /// sign-in did not produce a session; the caller is expected to degrade to an unauthenticated
    /// request, exactly as an indexer with no declared login already behaves.
    /// </summary>
    /// <param name="clearance">
    /// A solved browser challenge the site sits behind. Given, the sign-in goes out on the proxied
    /// client as the browser that solved it, and a challenge met on the way is thrown as an
    /// <see cref="IndexerChallengeException"/> — the caller renews the clearance — rather than being
    /// counted as a failed sign-in: nothing about the credential was tested.
    /// </param>
    Task<IReadOnlyList<JarCookie>?> EnsureSessionAsync(
        Guid indexerId,
        string indexerName,
        Uri baseUri,
        DefinitionSession session,
        IndexerCredential credential,
        BrowserClearance? clearance = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// The site refused the kept session. One that has worked before has simply expired, and the
    /// next <see cref="EnsureSessionAsync"/> signs in again. One that has not is a sign-in that only
    /// looked successful — a wrong password answered with a redirect — so it counts as a failed
    /// attempt and the backoff holds the next one off. <paramref name="cookies"/> is the jar that
    /// was refused: a refusal of one that has already been replaced (a concurrent search renewed it,
    /// or the credential changed) says nothing about its successor and is ignored.
    /// </summary>
    Task ReportRejectedAsync(
        Guid indexerId, string indexerName, IReadOnlyList<JarCookie> cookies, CancellationToken cancellationToken = default);

    /// <summary>
    /// A search got through with <paramref name="cookies"/>, the jar <see cref="EnsureSessionAsync"/>
    /// handed out: that session is proven, and the failed-attempt count behind it is cleared. A jar
    /// that has since been replaced proves nothing about its successor and is ignored.
    /// </summary>
    Task ConfirmAsync(Guid indexerId, IReadOnlyList<JarCookie> cookies, CancellationToken cancellationToken = default);
}

internal sealed class IndexerSessionManager(
    IHttpClientFactory httpClientFactory,
    IServiceScopeFactory scopeFactory,
    IndexerCredentialProtector protector,
    ILogger<IndexerSessionManager>? logger = null) : IIndexerSessionManager
{
    internal const string HttpClientName = "IndexerSession";

    /// <summary>
    /// How long a failed sign-in holds off the next one, doubling per consecutive failure and
    /// capped. A capped exponential rather than a hard stop after some number of attempts, because
    /// the usual cause is a tracker that is briefly unreachable or rate-limiting; an indexer that can
    /// never try again until an operator notices is a worse failure than one that tries twice a day.
    /// </summary>
    private static readonly TimeSpan FirstBackoff = TimeSpan.FromMinutes(5);

    private static readonly TimeSpan MaxBackoff = TimeSpan.FromHours(6);

    /// <summary>The bounded reasons a sign-in can fail. Span/log vocabulary, never a response quote.</summary>
    internal static class FailureReasons
    {
        public const string InvalidLoginUrl = "invalid_login_url";
        public const string InsecureLoginUrl = "insecure_login_url";
        public const string LoginPageUnreachable = "login_page_unreachable";
        public const string CsrfTokenMissing = "csrf_token_missing";
        public const string RequestRejected = "request_rejected";
        public const string LoginFormMatched = "login_form_matched";
        public const string NoCookies = "no_cookies";
        public const string NotStorable = "not_storable";
    }

    private readonly ConcurrentDictionary<Guid, SemaphoreSlim> _gates = new();
    private readonly ConcurrentDictionary<Guid, KeptSession> _sessions = new();

    private sealed record KeptSession(string CredentialFingerprint, IReadOnlyList<JarCookie> Cookies, bool Confirmed);

    public async Task<IReadOnlyList<JarCookie>?> EnsureSessionAsync(
        Guid indexerId,
        string indexerName,
        Uri baseUri,
        DefinitionSession session,
        IndexerCredential credential,
        BrowserClearance? clearance = null,
        CancellationToken cancellationToken = default)
    {
        var gate = _gates.GetOrAdd(indexerId, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken);
        try
        {
            // The fingerprint is a SHA-256 of the secret — computed, never stored and never logged —
            // and is what makes a replaced credential forget its predecessor's session.
            var fingerprint = Fingerprint(credential);
            if (_sessions.TryGetValue(indexerId, out var cached) && cached.CredentialFingerprint == fingerprint)
            {
                return cached.Cookies;
            }

            if (cached is not null)
            {
                _sessions.TryRemove(indexerId, out _);
            }

            var stored = await LoadAsync(indexerId, fingerprint, cancellationToken);
            if (stored.Jar is { Count: > 0 })
            {
                _sessions[indexerId] = new KeptSession(fingerprint, stored.Jar, stored.Confirmed);
                return stored.Jar;
            }

            if (stored.SigningInIsHeldOff)
            {
                // The last sign-in failed and its backoff has not elapsed. Trying anyway is how one
                // mistyped password becomes one credential submission per scheduled search, forever:
                // a pattern a private tracker reads as credential stuffing from this address, and
                // answers by banning the account. Nothing else bounds it, because a sign-in
                // deliberately spends no search quota.
                logger?.LogDebug(
                    "Indexer {IndexerName} is not being signed in again yet: the last attempt failed.",
                    indexerName);
                return null;
            }

            var captured = await LoginAsync(
                indexerId, indexerName, baseUri, session, credential, fingerprint, clearance, cancellationToken);
            if (captured is { Count: > 0 })
            {
                _sessions[indexerId] = new KeptSession(fingerprint, captured, Confirmed: false);
                return captured;
            }

            return null;
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task InvalidateAsync(Guid indexerId, CancellationToken cancellationToken = default)
    {
        // Under the gate, like every other mutation of the cache and the row. Without it a sign-in
        // running concurrently can write the row and re-populate the cache after this has run, which
        // is the outliving this call exists to prevent.
        var gate = _gates.GetOrAdd(indexerId, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken);
        try
        {
            await ForgetAsync(indexerId, cancellationToken);
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task ReportRejectedAsync(
        Guid indexerId, string indexerName, IReadOnlyList<JarCookie> cookies, CancellationToken cancellationToken = default)
    {
        var gate = _gates.GetOrAdd(indexerId, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken);
        try
        {
            // Every jar handed out is the one kept here, so a refused jar that is no longer kept was
            // already dealt with: charging its refusal to whatever replaced it would count a normal
            // expiry, seen by two concurrent searches, as a failed sign-in of the fresh session.
            if (!_sessions.TryGetValue(indexerId, out var cached) || !ReferenceEquals(cached.Cookies, cookies))
            {
                return;
            }

            _sessions.TryRemove(indexerId, out _);
            var proven = await RecordRejectionAsync(indexerId, cached.Confirmed, cancellationToken);
            if (!proven)
            {
                // The loop this closes: the site answers a wrong password with the same redirect it
                // answers a right one with, the jar is refused on the search, and — when refusal
                // wiped the attempt history — every search submitted the password twice, for ever.
                logger?.LogWarning(
                    "Indexer '{IndexerName}' refused the session it had just signed in with; this counts as a failed sign-in.",
                    indexerName);
                DiscoveryMetrics.RecordLoginFailure(indexerName);
            }
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task ConfirmAsync(
        Guid indexerId, IReadOnlyList<JarCookie> cookies, CancellationToken cancellationToken = default)
    {
        // Checked before the gate too: every successful search lands here, and all but the first
        // one per session have nothing to do.
        if (!IsUnconfirmed(indexerId, cookies))
        {
            return;
        }

        var gate = _gates.GetOrAdd(indexerId, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken);
        try
        {
            if (!IsUnconfirmed(indexerId, cookies))
            {
                return;
            }

            _sessions[indexerId] = _sessions[indexerId] with { Confirmed = true };
            await using var scope = scopeFactory.CreateAsyncScope();
            var dbContext = scope.ServiceProvider.GetRequiredService<DiscoveryDbContext>();
            var row = await dbContext.IndexerSessions
                .FirstOrDefaultAsync(s => s.IndexerId == indexerId, cancellationToken);
            if (row?.CookiesCipher is not null && row.ConfirmedAt is null)
            {
                row.Confirm(DateTimeOffset.UtcNow);
                await dbContext.SaveChangesAsync(cancellationToken);
            }
        }
        catch (Exception failure) when (failure is not OperationCanceledException)
        {
            // This process already knows the session works. After a restart the row still reads
            // as unproven, which costs at most one counted failure if the site expires it first.
            logger?.LogWarning(
                "An indexer session could not be marked as working ({FailureType}).",
                failure.GetType().Name);
        }
        finally
        {
            gate.Release();
        }
    }

    private bool IsUnconfirmed(Guid indexerId, IReadOnlyList<JarCookie> cookies) =>
        _sessions.TryGetValue(indexerId, out var kept)
        && !kept.Confirmed
        && ReferenceEquals(kept.Cookies, cookies);

    /// <summary>
    /// Writes down a refusal, and says whether the refused session had been proven. Proven, it
    /// expired: the cookies go and the history stays clean. Unproven, the sign-in behind it failed,
    /// and the row counts it like any other failed attempt.
    /// </summary>
    private async Task<bool> RecordRejectionAsync(Guid indexerId, bool confirmedInMemory, CancellationToken cancellationToken)
    {
        var proven = confirmedInMemory;
        try
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var dbContext = scope.ServiceProvider.GetRequiredService<DiscoveryDbContext>();
            var row = await dbContext.IndexerSessions
                .FirstOrDefaultAsync(s => s.IndexerId == indexerId, cancellationToken);
            proven = proven || row?.ConfirmedAt is not null;
            if (proven)
            {
                row?.Expire();
            }
            else
            {
                if (row is null)
                {
                    row = new IndexerSession { IndexerId = indexerId };
                    dbContext.IndexerSessions.Add(row);
                }

                row.RecordFailedAttempt(DateTimeOffset.UtcNow);
            }

            await dbContext.SaveChangesAsync(cancellationToken);
        }
        catch (Exception failure) when (failure is not OperationCanceledException)
        {
            logger?.LogWarning(
                "An indexer session refusal could not be recorded ({FailureType}).",
                failure.GetType().Name);
        }

        return proven;
    }

    private async Task ForgetAsync(Guid indexerId, CancellationToken cancellationToken)
    {
        _sessions.TryRemove(indexerId, out _);
        try
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var dbContext = scope.ServiceProvider.GetRequiredService<DiscoveryDbContext>();
            var row = await dbContext.IndexerSessions
                .FirstOrDefaultAsync(s => s.IndexerId == indexerId, cancellationToken);
            if (row is not null)
            {
                dbContext.IndexerSessions.Remove(row);
                await dbContext.SaveChangesAsync(cancellationToken);
            }
        }
        catch (Exception failure) when (failure is not OperationCanceledException)
        {
            // The cache is already dropped, so the next search signs in regardless; a leftover row
            // only misstates the listing. Worth one line, never a failed search.
            logger?.LogWarning(
                "An indexer session row could not be removed ({FailureType}).",
                failure.GetType().Name);
        }
    }

    /// <summary>
    /// A SHA-256 over the whole credential — username and secret, length-delimited so that no pair of
    /// accounts can hash to the same bytes by moving the boundary. In memory for one dictionary lookup
    /// and gone after: never stored, never logged.
    /// <para>
    /// The username belongs in it as much as the password does. Hashing the secret alone would make
    /// two accounts sharing one password indistinguishable, and correcting a mistyped username would
    /// then keep signing in as the previous one.
    /// </para>
    /// </summary>
    private static string Fingerprint(IndexerCredential credential)
    {
        var username = credential.Username ?? string.Empty;
        var material = $"{username.Length}:{username}{credential.Secret}";
        return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(material)));
    }

    // -- login --------------------------------------------------------------------------------------

    private async Task<IReadOnlyList<JarCookie>?> LoginAsync(
        Guid indexerId,
        string indexerName,
        Uri baseUri,
        DefinitionSession session,
        IndexerCredential credential,
        string credentialFingerprint,
        BrowserClearance? clearance,
        CancellationToken cancellationToken)
    {
        using var activity = CinomniTelemetry.Source.StartActivity("indexer.login", ActivityKind.Client);
        activity?.SetTag(CinomniTelemetry.Tags.Module, CinomniTelemetry.Modules.Discovery);
        activity?.SetTag(CinomniTelemetry.Tags.IndexerName, indexerName);

        IReadOnlyList<JarCookie> jar = [];
        try
        {
            var loginUrl = ResolveLoginUrl(baseUri, session.Login);
            if (loginUrl is null)
            {
                return await FailAsync(indexerId, indexerName, activity, FailureReasons.InvalidLoginUrl, cancellationToken);
            }

            if (!IsSecure(loginUrl))
            {
                return await FailAsync(indexerId, indexerName, activity, FailureReasons.InsecureLoginUrl, cancellationToken);
            }

            // Resolved per sequence rather than captured once: a client held for the life of the
            // process pins one handler past its rotation window, so DNS is never refreshed and a
            // tracker that moves keeps resolving to a stale address until the Host restarts.
            // Behind a browser challenge the sequence goes out on the proxied client — the address the
            // clearance is bound to — and as the browser that solved it.
            var httpClient = httpClientFactory.CreateClient(
                clearance is null ? HttpClientName : IndexerClearanceCache.HttpClientName);

            string? csrfToken = null;
            if (session.Login.CsrfToken is { } csrf)
            {
                using var pageRequest = new HttpRequestMessage(HttpMethod.Get, loginUrl);
                if (clearance is not null)
                {
                    BrowserChallenge.Apply(pageRequest, clearance, session: null);
                }

                using var page = await httpClient.SendAsync(pageRequest, cancellationToken);
                var pageBody = await page.Content.ReadAsStringAsync(cancellationToken);
                ThrowIfChallenged(page, pageBody, clearance);
                jar = IndexerSessionCookies.Merge(jar, IndexerSessionCookies.Capture(page, loginUrl));
                if (!page.IsSuccessStatusCode)
                {
                    return await FailAsync(indexerId, indexerName, activity, FailureReasons.LoginPageUnreachable, cancellationToken);
                }

                csrfToken = ReadCsrfToken(pageBody, csrf);
                if (csrfToken is null)
                {
                    return await FailAsync(indexerId, indexerName, activity, FailureReasons.CsrfTokenMissing, cancellationToken);
                }
            }

            using var request = BuildLoginRequest(session.Login, loginUrl, credential, csrfToken);
            // Whatever the login page set travels back with the submit. A site that issues a CSRF
            // token almost always pairs it with a session cookie and validates the two together, so
            // a submit sent bare would be rejected by exactly the sites this sequence exists for.
            if (clearance is not null)
            {
                BrowserChallenge.Apply(request, clearance, jar);
            }
            else if (IndexerSessionCookies.HeaderFor(jar, request.RequestUri!) is { } carried)
            {
                request.Headers.TryAddWithoutValidation("Cookie", carried);
            }

            using var response = await httpClient.SendAsync(request, cancellationToken);
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            ThrowIfChallenged(response, body, clearance);
            // Merged, never appended: a site that rotates its session id at sign-in — the correct
            // defence against session fixation — reuses the same cookie name, and a jar holding both
            // would send both on every search afterwards.
            jar = IndexerSessionCookies.Merge(jar, IndexerSessionCookies.Capture(response, loginUrl));
            if (clearance is not null)
            {
                // A challenge cookie refreshed on the way is the clearance's, not the session's: kept
                // in the jar it would be stored at rest and later override the live clearance.
                jar = BrowserChallenge.WithoutClearance(jar);
            }

            // A redirect after the submit is the site's own "signed in, moving along"; a landed page
            // is where the check selector answers. The same selector that means "the site expired
            // the session" on a search means "the sign-in failed" here — that session is not kept.
            var redirected = response.StatusCode is HttpStatusCode.MultipleChoices
                or HttpStatusCode.Moved or HttpStatusCode.Found
                or HttpStatusCode.TemporaryRedirect or HttpStatusCode.PermanentRedirect;
            if (!redirected)
            {
                if (session.Check is { } check && DefinitionSessionMatcher.IsLoggedOut(body, check.Selector))
                {
                    return await FailAsync(indexerId, indexerName, activity, FailureReasons.LoginFormMatched, cancellationToken);
                }

                if (!response.IsSuccessStatusCode)
                {
                    return await FailAsync(indexerId, indexerName, activity, FailureReasons.RequestRejected, cancellationToken);
                }
            }

            if (jar.Count == 0)
            {
                // Nothing was captured across the whole sequence: there is no session to keep or to
                // send on the next search, so "signed in" would be a claim with nothing behind it.
                return await FailAsync(indexerId, indexerName, activity, FailureReasons.NoCookies, cancellationToken);
            }

            var encrypted = protector.EncryptCookies(
                indexerId, credentialFingerprint, IndexerSessionCookies.Serialize(jar));
            if (encrypted.IsFailure)
            {
                return await FailAsync(indexerId, indexerName, activity, FailureReasons.NotStorable, cancellationToken);
            }

            // Guarded on purpose, and after the sign-in has already succeeded: a jar that works but
            // was not written down is strictly better than none, because the caller can search with
            // it now and the next process re-derives it by signing in again. Letting a failed insert
            // fall into the catch below would discard a live session and search the tracker
            // anonymously instead.
            await TryPersistAsync(indexerId, indexerName, encrypted.Value, cancellationToken);
            DiscoveryMetrics.RecordLogin(indexerName);
            return jar;
        }
        catch (Exception failure) when (failure is not (OperationCanceledException or IndexerChallengeException))
        {
            // The exception's message can quote the request URL (which travelled with the password)
            // or a Set-Cookie value, so only its type is worth a log line. Never the message.
            logger?.LogWarning(
                "Indexer '{IndexerName}' could not sign in ({FailureType}).",
                indexerName,
                failure.GetType().Name);
            activity?.SetStatus(ActivityStatusCode.Error);
            activity?.SetTag(CinomniTelemetry.Tags.Reason, FailureReasons.RequestRejected);
            DiscoveryMetrics.RecordLoginFailure(indexerName);
            await RecordFailureAsync(indexerId, cancellationToken);
            return null;
        }
    }

    private async Task<IReadOnlyList<JarCookie>?> FailAsync(
        Guid indexerId, string indexerName, Activity? activity, string reason, CancellationToken cancellationToken)
    {
        activity?.SetStatus(ActivityStatusCode.Error);
        activity?.SetTag(CinomniTelemetry.Tags.Reason, reason);
        logger?.LogWarning("Indexer '{IndexerName}' could not sign in ({Reason}).", indexerName, reason);
        DiscoveryMetrics.RecordLoginFailure(indexerName);
        await RecordFailureAsync(indexerId, cancellationToken);
        return null;
    }

    /// <summary>
    /// A challenge on the way to the site says the clearance lapsed, and nothing about the credential:
    /// thrown past the failure bookkeeping, so it costs no backoff, for the caller to renew and retry.
    /// </summary>
    /// <remarks>
    /// Without a clearance too: a site behind a challenge refuses every sign-in the same way, and
    /// counting that as a wrong password held the next one off for minutes after FlareSolverr was
    /// turned on — the one change that would have let it through.
    /// </remarks>
    private static void ThrowIfChallenged(HttpResponseMessage response, string body, BrowserClearance? clearance)
    {
        if (BrowserChallenge.IsChallenge(response, body))
        {
            throw new IndexerChallengeException(clearance is null
                ? "The sign-in met a browser challenge; this indexer needs FlareSolverr turned on."
                : "The sign-in met a browser challenge.");
        }
    }

    private static Uri? ResolveLoginUrl(Uri baseUri, DefinitionSessionLogin login) =>
        // The parser has already rejected anything that is neither root-relative nor absolute
        // http(s); SameOrigin keeps a definition from pointing its credentials at another host, and
        // refusing userinfo keeps a credential out of the one part of a URL that the transport
        // ignores and log redaction does not cover.
        DefinitionQueryBuilder.TryResolve(baseUri, login.UrlTemplate, out var resolved)
        && DefinitionQueryBuilder.SameOrigin(baseUri, resolved)
        && string.IsNullOrEmpty(resolved.UserInfo)
            ? resolved
            : null;

    /// <summary>
    /// A sign-in only runs over TLS. Everything a login sequence carries travels in the clear
    /// otherwise: the password on the way out, the session jar on the way back, and that jar again on
    /// every search after it. Unlike an indexer API key, an account password is usually reused
    /// somewhere else. The SSRF guard already refuses private addresses, so plain http here means a
    /// public site over cleartext, and this fails closed rather than degrading quietly.
    /// </summary>
    private static bool IsSecure(Uri loginUrl) => loginUrl.Scheme == Uri.UriSchemeHttps;

    private static HttpRequestMessage BuildLoginRequest(
        DefinitionSessionLogin login, Uri loginUrl, IndexerCredential credential, string? csrfToken)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["credential.username"] = credential.Username ?? string.Empty,
            ["credential.password"] = credential.Secret,
            ["csrfToken"] = csrfToken ?? string.Empty,
        };

        string Substitute(string template) =>
            FieldTransforms.PlaceholderPattern().Replace(
                template, match => values.TryGetValue(match.Groups[1].Value, out var value) ? value : match.Value);

        if (login.Method == DefinitionHttpMethod.Post)
        {
            // FormUrlEncodedContent percent-encodes both sides — a password containing '&' or '='
            // cannot smuggle an extra form field, mirroring the URL query builder's discipline.
            return new HttpRequestMessage(HttpMethod.Post, loginUrl)
            {
                Content = new FormUrlEncodedContent(
                    login.Fields.Select(field => new KeyValuePair<string, string>(field.Name, Substitute(field.ValueTemplate)))),
            };
        }

        var query = string.Join(
            "&",
            login.Fields.Select(field => $"{FieldTransforms.UrlEncode(field.Name)}={FieldTransforms.UrlEncode(Substitute(field.ValueTemplate))}"));
        var target = new Uri(
            loginUrl.GetLeftPart(UriPartial.Query) + (loginUrl.Query.Length > 0 ? "&" : "?") + query);
        return new HttpRequestMessage(HttpMethod.Get, target);
    }

    private static string? ReadCsrfToken(string html, DefinitionCsrfToken csrf)
    {
        var element = new HtmlParser().ParseDocument(html).QuerySelector(csrf.Selector);
        var raw = DefinitionElementValue.Read(element, csrf.Attribute);
        return string.IsNullOrWhiteSpace(raw) ? null : raw.Trim();
    }

    // -- persistence --------------------------------------------------------------------------------

    /// <summary>What the stored row says: the usable jar, if any, whether a search has proven it, and whether a new sign-in is held off.</summary>
    private readonly record struct StoredSession(IReadOnlyList<JarCookie>? Jar, bool Confirmed, bool SigningInIsHeldOff);

    /// <summary>
    /// The jar kept for this indexer <em>under this credential</em>, plus the failed-attempt verdict.
    /// A row written for a different credential fails to authenticate and reads as no session, so a
    /// credential change re-signs-in by construction rather than by anyone remembering to delete the
    /// row first.
    /// </summary>
    private async Task<StoredSession> LoadAsync(
        Guid indexerId, string credentialFingerprint, CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<DiscoveryDbContext>();
        var row = await dbContext.IndexerSessions.AsNoTracking()
            .FirstOrDefaultAsync(s => s.IndexerId == indexerId, cancellationToken);
        if (row is null)
        {
            return new StoredSession(null, false, false);
        }

        var json = protector.TryDecryptCookies(
            indexerId, credentialFingerprint, row.CookiesCipher, row.CookiesNonce, row.CookiesKeyId);
        var jar = IndexerSessionCookies.Deserialize(json);
        return new StoredSession(
            jar.Count > 0 ? jar : null, row.ConfirmedAt is not null, IsHeldOff(row, DateTimeOffset.UtcNow));
    }

    /// <summary>
    /// Whether the last attempt failed recently enough that another one would be hammering. The
    /// window doubles per consecutive failure from <see cref="FirstBackoff"/> and stops at
    /// <see cref="MaxBackoff"/>; the shift is bounded so a long-broken indexer cannot overflow it.
    /// </summary>
    private static bool IsHeldOff(IndexerSession row, DateTimeOffset now)
    {
        if (row.LastAttemptOk)
        {
            return false;
        }

        var doublings = Math.Clamp(row.ConsecutiveFailures - 1, 0, 16);
        var window = TimeSpan.FromTicks(Math.Min(FirstBackoff.Ticks << doublings, MaxBackoff.Ticks));
        return now < row.LastAttemptAt + window;
    }

    private async Task TryPersistAsync(
        Guid indexerId, string indexerName, EncryptedSecret encrypted, CancellationToken cancellationToken)
    {
        try
        {
            await PersistAsync(indexerId, encrypted, DateTimeOffset.UtcNow, cancellationToken);
        }
        catch (Exception failure) when (failure is not OperationCanceledException)
        {
            logger?.LogWarning(
                "The session for indexer {IndexerName} could not be stored ({FailureType}); it is kept for this process only.",
                indexerName,
                failure.GetType().Name);
        }
    }

    private async Task PersistAsync(Guid indexerId, EncryptedSecret encrypted, DateTimeOffset capturedAt, CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<DiscoveryDbContext>();
        var row = await dbContext.IndexerSessions
            .FirstOrDefaultAsync(s => s.IndexerId == indexerId, cancellationToken);
        if (row is null)
        {
            row = new IndexerSession { IndexerId = indexerId };
            dbContext.IndexerSessions.Add(row);
        }

        row.Store(encrypted.Cipher, encrypted.Nonce, encrypted.KeyId, capturedAt);
        await dbContext.SaveChangesAsync(cancellationToken);
    }

    private async Task RecordFailureAsync(Guid indexerId, CancellationToken cancellationToken)
    {
        try
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var dbContext = scope.ServiceProvider.GetRequiredService<DiscoveryDbContext>();
            var row = await dbContext.IndexerSessions
                .FirstOrDefaultAsync(s => s.IndexerId == indexerId, cancellationToken);
            if (row is null)
            {
                row = new IndexerSession { IndexerId = indexerId };
                dbContext.IndexerSessions.Add(row);
            }

            row.RecordFailedAttempt(DateTimeOffset.UtcNow);
            await dbContext.SaveChangesAsync(cancellationToken);
        }
        catch (Exception failure) when (failure is not OperationCanceledException)
        {
            // Attempt bookkeeping is a report for the listing, never a prerequisite of searching;
            // the sign-in result the caller acts on was already decided above.
            logger?.LogWarning(
                "An indexer session attempt could not be recorded ({FailureType}).",
                failure.GetType().Name);
        }
    }
}
