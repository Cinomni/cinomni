using System.Net;
using System.Text;
using Cinomni.Discovery.Application;
using Cinomni.Discovery.Contracts;
using Cinomni.Discovery.Indexers;
using Cinomni.Discovery.Indexers.Definition;
using Cinomni.Discovery.Persistence;
using Cinomni.Operations;
using Cinomni.Operations.Transactions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Cinomni.Discovery.Tests;

/// <summary>
/// The login sequence end to end, against a fake tracker transport and a real PostgreSQL: the
/// declared session block is parsed, executed with the stored credential, its cookies captured and
/// kept encrypted, and the search carries them — plus what happens when the site expires the session
/// (one re-login, one retry, then give up). The fake transport never sees a real credential in a URL
/// or a log, and every assertion here is one a private-tracker operator would recognize.
/// </summary>
[Collection(IndexerSecretsCollection.Serial)]
[Trait("Category", "RequiresDatabase")]
public sealed class IndexerSessionTests : IAsyncLifetime
{
    /// <summary>32 fixed bytes. A test key, never a real one, and it never leaves this file.</summary>
    private const string MasterKeyBase64 = "AQIDBAUGBwgJCgsMDQ4PEBESExQVFhcYGRobHB0eHyA=";

    private const string Database = "cinomni_test_discovery_indexer_sessions";

    private const string LoginDefinitionJson = """
        {
          "schemaVersion": 1,
          "resultKind": "Torrent",
          "search": {
            "requests": [{ "contentKinds": ["Movie"], "method": "Get", "urlTemplate": "/search?q={{term}}" }],
            "responseFormat": "Html",
            "rows": { "selector": "tr.result", "maxRows": 50 },
            "fields": {
              "title": { "selector": "td.name a", "attribute": "Text" },
              "downloadUrl": { "selector": "td.dl a", "attribute": "Href" }
            }
          },
          "session": {
            "login": {
              "method": "Post",
              "urlTemplate": "/login",
              "fields": [
                { "name": "username", "valueTemplate": "{{credential.username}}" },
                { "name": "password", "valueTemplate": "{{credential.password}}" },
                { "name": "csrf", "valueTemplate": "{{csrfToken}}" }
              ],
              "csrfToken": { "selector": "span#csrf", "attribute": "Text" }
            },
            "check": { "selector": "form#login" }
          }
        }
        """;

    private const string LoginHtml = """
            <html><body>
              <span id="csrf">tok-1</span>
              <form id="login"><input name="username"><input name="password"></form>
            </body></html>
        """;

    private const string SearchHtml = """
            <table>
              <tr class="result">
                <td class="name"><a href="/details/1">Interstellar 2014 1080p</a></td>
                <td class="dl"><a href="https://tracker.example/dl/1.torrent">DL</a></td>
              </tr>
            </table>
        """;

    private static readonly IndexerCredential Credential = new("operator", "hunter2");

    private string? _previousKey;
    private ServiceProvider _provider = null!;

    public async Task InitializeAsync()
    {
        _previousKey = Environment.GetEnvironmentVariable("CINOMNI_SECRET_KEY");
        Environment.SetEnvironmentVariable("CINOMNI_SECRET_KEY", MasterKeyBase64);

        _provider = await DiscoveryTestHost.CreateAsync(Database);
    }

    public async Task DisposeAsync()
    {
        await _provider.DisposeAsync();
        Environment.SetEnvironmentVariable("CINOMNI_SECRET_KEY", _previousKey);
    }

    [Fact]
    public async Task The_declared_login_runs_with_the_credential_and_keeps_its_cookies()
    {
        var tracker = new TrackerHandler();
        var sessions = NewSessionManager(tracker);
        var indexerId = await AddSessionIndexerAsync("Login");

        var jar = await sessions.EnsureSessionAsync(
            indexerId.Value, "Login", new Uri("https://tracker.example/"), SessionBlock(), Credential);

        // The sequence: read the CSRF token from the login page, then submit the form. The cookie
        // the page set travels with the submit, because the sequence's cookies are captured from
        // every response and sent with every request after it.
        Assert.Equal(2, tracker.Requests.Count(r => r.Url.AbsolutePath == "/login"));
        var submit = Assert.Single(tracker.Requests, r => r.Url.AbsolutePath == "/login" && r.Method == "POST");
        Assert.Contains("username=operator", submit.Body, StringComparison.Ordinal);
        Assert.Contains("password=hunter2", submit.Body, StringComparison.Ordinal);
        Assert.Contains("csrf=tok-1", submit.Body, StringComparison.Ordinal);
        Assert.Equal("pre=1", submit.Cookie);

        Assert.NotNull(jar);
        Assert.Contains(jar!, cookie => cookie.Name == "session" && cookie.Value == "abc123");

        // Kept at rest, encrypted: the row holds ciphertext, never the jar in the clear, and the
        // attempt bookkeeping says the sign-in worked.
        await using var scope = _provider.CreateAsyncScope();
        var row = await scope.ServiceProvider.GetRequiredService<DiscoveryDbContext>().IndexerSessions
            .SingleAsync(s => s.IndexerId == indexerId.Value);
        Assert.NotNull(row.CookiesCipher);
        Assert.NotNull(row.CookiesKeyId);
        Assert.NotNull(row.CapturedAt);
        Assert.True(row.LastAttemptOk);
        Assert.NotEqual(
            IndexerSessionCookies.Serialize(jar!),
            Encoding.UTF8.GetString(row.CookiesCipher!));
    }

    [Fact]
    public async Task Concurrent_searches_share_one_sign_in()
    {
        var tracker = new TrackerHandler();
        var sessions = NewSessionManager(tracker);
        var indexerId = await AddSessionIndexerAsync("Raced");

        var first = sessions.EnsureSessionAsync(
            indexerId.Value, "Raced", new Uri("https://tracker.example/"), SessionBlock(), Credential);
        var second = sessions.EnsureSessionAsync(
            indexerId.Value, "Raced", new Uri("https://tracker.example/"), SessionBlock(), Credential);
        var jars = await Task.WhenAll(first, second);

        // A tracker counts login attempts; two concurrent searches must cost one sign-in, not two.
        Assert.Equal(1, tracker.Requests.Count(r => r.Url.AbsolutePath == "/login" && r.Method == "POST"));
        Assert.Equal(jars[0], jars[1]);
    }

    [Fact]
    public async Task A_failed_sign_in_is_reported_and_not_kept()
    {
        var tracker = new TrackerHandler { LoginSucceeds = false };
        var sessions = NewSessionManager(tracker);
        var indexerId = await AddSessionIndexerAsync("Refused");

        var jar = await sessions.EnsureSessionAsync(
            indexerId.Value, "Refused", new Uri("https://tracker.example/"), SessionBlock(), Credential);

        // The same check that detects expiry on a search detects failure on a landed login page:
        // the form is still there, so nothing is kept and the caller degrades to unauthenticated.
        Assert.Null(jar);

        await using var scope = _provider.CreateAsyncScope();
        var row = await scope.ServiceProvider.GetRequiredService<DiscoveryDbContext>().IndexerSessions
            .SingleAsync(s => s.IndexerId == indexerId.Value);
        Assert.Null(row.CookiesCipher);
        Assert.False(row.LastAttemptOk);
    }

    [Fact]
    public async Task A_failed_sign_in_holds_off_the_next_one_instead_of_retrying_every_search()
    {
        var tracker = new TrackerHandler { LoginSucceeds = false };
        var indexerId = await AddSessionIndexerAsync("Wrong");
        var sessions = NewSessionManager(tracker);

        for (var attempt = 0; attempt < 3; attempt++)
        {
            Assert.Null(await sessions.EnsureSessionAsync(
                indexerId.Value, "Wrong", new Uri("https://tracker.example/"), SessionBlock(), Credential));
        }

        // One attempt, not three. A wrong stored password would otherwise be submitted once per
        // scheduled search for ever, which a private tracker reads as credential stuffing from this
        // address and answers by banning the account — a failure no restart here can undo.
        Assert.Equal(1, tracker.Requests.Count(r => r.Url.AbsolutePath == "/login" && r.Method == "POST"));

        await using var scope = _provider.CreateAsyncScope();
        var row = await scope.ServiceProvider.GetRequiredService<DiscoveryDbContext>().IndexerSessions
            .SingleAsync(s => s.IndexerId == indexerId.Value);
        Assert.False(row.LastAttemptOk);
        Assert.Equal(1, row.ConsecutiveFailures);
    }

    [Fact]
    public async Task A_login_is_refused_over_plain_http_before_anything_is_sent()
    {
        var tracker = new TrackerHandler();
        var indexerId = await AddSessionIndexerAsync("Cleartext");
        var sessions = NewSessionManager(tracker);

        var jar = await sessions.EnsureSessionAsync(
            indexerId.Value, "Cleartext", new Uri("http://tracker.example/"), SessionBlock(), Credential);

        // Fails closed, and closed means nothing left the process: the password, and then the jar on
        // every later search, would otherwise cross the network in the clear.
        Assert.Null(jar);
        Assert.Empty(tracker.Requests);
    }

    [Fact]
    public async Task A_search_bounced_to_the_login_page_is_expiry_and_not_an_empty_result()
    {
        var tracker = new TrackerHandler();
        tracker.PlanSearch(HttpStatusCode.OK); // the session works once, so it is proven
        tracker.PlanSearchRedirectToLogin();
        var indexerId = await AddSessionIndexerAsync("Bounced");
        var client = NewClient(tracker);
        await SearchAsync(client, indexerId);

        var candidates = await SearchAsync(client, indexerId);

        // The transport never follows redirects, so this arrives with an empty body: no status the
        // check knows and nothing for the check selector to match. Read as a search result it would
        // be a confident "this title is not on the site".
        Assert.Single(candidates);
        Assert.Equal(2, tracker.Requests.Count(r => r.Url.AbsolutePath == "/login" && r.Method == "POST"));
    }

    [Fact]
    public async Task A_server_error_on_the_session_path_fails_the_search_rather_than_emptying_it()
    {
        var tracker = new TrackerHandler { SearchStatus = HttpStatusCode.InternalServerError };
        var indexerId = await AddSessionIndexerAsync("Broken");
        var sessions = NewSessionManager(tracker);
        var client = new DefinitionIndexerClient(
            new HttpClient(tracker), logger: null, sessionManager: sessions);

        // The same 500 on an indexer without a session already throws; the session path must not be
        // the one place where a broken site reports zero releases and a healthy metric.
        await Assert.ThrowsAsync<HttpRequestException>(() => client.SearchAsync(
            Summary(indexerId), Credential, LoginDefinitionJson,
            new Cinomni.Search.Contracts.SearchCriterion("Interstellar", 2014, null, null, "Movie")));

        Assert.Single(tracker.SearchRequests());
    }

    [Fact]
    public async Task A_kept_session_survives_a_restart_and_is_reused_without_signing_in_again()
    {
        var tracker = new TrackerHandler();
        var indexerId = await AddSessionIndexerAsync("Restarted");
        var manager = NewSessionManager(tracker);
        var first = await manager.EnsureSessionAsync(
            indexerId.Value, "Restarted", new Uri("https://tracker.example/"), SessionBlock(), Credential);
        Assert.NotNull(first);

        // Dispose the provider the way a process restart would, and rebuild over the same database.
        var database = _provider;
        _provider = await RebuildProviderAsync();
        await database.DisposeAsync();

        var restartedTracker = new TrackerHandler();
        var restarted = NewSessionManager(restartedTracker);
        var second = await restarted.EnsureSessionAsync(
            indexerId.Value, "Restarted", new Uri("https://tracker.example/"), SessionBlock(), Credential);

        Assert.Equal(first, second);
        Assert.DoesNotContain(restartedTracker.Requests, r => r.Url.AbsolutePath == "/login");
    }

    [Fact]
    public async Task A_session_stored_under_another_credential_is_not_readable_after_a_restart()
    {
        var tracker = new TrackerHandler();
        var indexerId = await AddSessionIndexerAsync("Rekeyed");
        await NewSessionManager(tracker).EnsureSessionAsync(
            indexerId.Value, "Rekeyed", new Uri("https://tracker.example/"), SessionBlock(), Credential);

        // A restart is where the in-memory fingerprint cannot help: the row is all there is, and it
        // carries no column saying whose it was. What refuses it is the jar's own encryption — the
        // credential is part of the additional authenticated data — so the row simply does not
        // decrypt for another account and reads as no session at all.
        var previous = _provider;
        _provider = await RebuildProviderAsync();
        await previous.DisposeAsync();

        var restartedTracker = new TrackerHandler();
        var jar = await NewSessionManager(restartedTracker).EnsureSessionAsync(
            indexerId.Value,
            "Rekeyed",
            new Uri("https://tracker.example/"),
            SessionBlock(),
            new IndexerCredential("operator", "a-different-password"));

        Assert.NotNull(jar);
        Assert.Equal(
            1,
            restartedTracker.Requests.Count(r => r.Url.AbsolutePath == "/login" && r.Method == "POST"));
    }

    [Fact]
    public async Task Replacing_the_credential_signs_in_again_with_the_new_one()
    {
        var tracker = new TrackerHandler();
        var sessions = NewSessionManager(tracker);
        var indexerId = await AddSessionIndexerAsync("Rotated");
        await sessions.EnsureSessionAsync(
            indexerId.Value, "Rotated", new Uri("https://tracker.example/"), SessionBlock(), Credential);

        var replaced = new IndexerCredential("operator", "new-password");
        var jar = await sessions.EnsureSessionAsync(
            indexerId.Value, "Rotated", new Uri("https://tracker.example/"), SessionBlock(), replaced);

        // A kept session authenticates the account it signed in as; a different credential means a
        // different account, so the old jar is dropped and the sequence runs again with the new one.
        Assert.Equal(2, tracker.Requests.Count(r => r.Url.AbsolutePath == "/login" && r.Method == "POST"));
        var submit = tracker.Requests.Last(r => r.Url.AbsolutePath == "/login" && r.Method == "POST");
        Assert.Contains("password=new-password", submit.Body, StringComparison.Ordinal);
        Assert.NotNull(jar);
    }

    [Fact]
    public async Task The_search_carries_the_session_and_a_site_expiry_costs_exactly_one_relogin_and_one_retry()
    {
        var tracker = new TrackerHandler();
        tracker.PlanSearch(HttpStatusCode.OK); // the session works once, so it is proven
        tracker.PlanSearch(HttpStatusCode.Forbidden); // then the site expires it
        var indexerId = await AddSessionIndexerAsync("Expired");
        var client = NewClient(tracker);
        await SearchAsync(client, indexerId);

        var candidates = await SearchAsync(client, indexerId);

        // The retry went out with fresh cookies and parsed normally, and an expiry is not a failure.
        Assert.Single(candidates);
        Assert.Equal(3, tracker.SearchRequests().Count);
        Assert.Equal(0, (await SessionRowAsync(indexerId)).ConsecutiveFailures);
        Assert.All(
            tracker.SearchRequests(),
            search => Assert.Contains("session=abc123", search.Cookie!, StringComparison.Ordinal));
        Assert.Equal(2, tracker.Requests.Count(r => r.Url.AbsolutePath == "/login" && r.Method == "POST"));
    }

    [Fact]
    public async Task A_search_that_is_expired_again_after_signing_in_gives_up_through_the_isolation_boundary()
    {
        var tracker = new TrackerHandler();
        tracker.PlanSearch(HttpStatusCode.OK); // proven
        var indexerId = await AddSessionIndexerAsync("Stubborn");
        var client = NewClient(tracker);
        await SearchAsync(client, indexerId);
        tracker.SearchStatus = HttpStatusCode.Forbidden;

        // ReleaseSearch.SearchOneAsync catches this and records a search failure; the adapter's job
        // is to refuse to go on querying a login-walled site unauthenticated forever.
        await Assert.ThrowsAsync<InvalidOperationException>(() => SearchAsync(client, indexerId));

        Assert.Equal(3, tracker.SearchRequests().Count);
        // Exactly one re-login: bounded, like every other retry in this client. The fresh session
        // was refused before it ever worked, so that one is a failed sign-in.
        Assert.Equal(2, tracker.Requests.Count(r => r.Url.AbsolutePath == "/login" && r.Method == "POST"));
        Assert.Equal(1, (await SessionRowAsync(indexerId)).ConsecutiveFailures);
    }

    [Fact]
    public async Task A_logged_out_page_that_looks_successful_is_expiry_too()
    {
        // The whole point of the check selector: a 200 whose body is the login form is not "no
        // results", it is "signed out" — and it must not be reported as a healthy empty search.
        var tracker = new TrackerHandler();
        tracker.PlanSearch(HttpStatusCode.OK); // proven
        tracker.PlanSearch(HttpStatusCode.OK, LoginHtml);
        tracker.PlanSearch(HttpStatusCode.OK, LoginHtml);
        var indexerId = await AddSessionIndexerAsync("Silent");
        var client = NewClient(tracker);
        await SearchAsync(client, indexerId);

        await Assert.ThrowsAsync<InvalidOperationException>(() => SearchAsync(client, indexerId));

        Assert.Equal(2, tracker.Requests.Count(r => r.Url.AbsolutePath == "/login" && r.Method == "POST"));
    }

    [Fact]
    public async Task A_wrong_password_answered_with_a_redirect_is_not_submitted_on_every_search()
    {
        // Arrange — the site answers every sign-in with a redirect and a cookie, right password or
        // not, and then refuses the cookie on the search: a wrong password, seen from here.
        var tracker = new TrackerHandler { SearchStatus = HttpStatusCode.Forbidden };
        var indexerId = await AddSessionIndexerAsync("Mistyped");
        var client = NewClient(tracker);

        // Act — three scheduled searches.
        for (var search = 0; search < 3; search++)
        {
            await Assert.ThrowsAnyAsync<Exception>(() => SearchAsync(client, indexerId));
        }

        // Assert — one submission, not two per search: the refusal counted as a failed sign-in, and
        // its backoff held off both the immediate renewal and every search after it.
        Assert.Equal(1, tracker.Requests.Count(r => r.Url.AbsolutePath == "/login" && r.Method == "POST"));
        var row = await SessionRowAsync(indexerId);
        Assert.False(row.LastAttemptOk);
        Assert.Equal(1, row.ConsecutiveFailures);
        Assert.Null(row.CookiesCipher);

        // Once the backoff lapses it tries again, and a second refusal widens the backoff instead of
        // starting it over: signing in does not clear the count, only a search that works does.
        await ExpireBackoffAsync(indexerId);
        await Assert.ThrowsAnyAsync<Exception>(() => SearchAsync(client, indexerId));
        await Assert.ThrowsAnyAsync<Exception>(() => SearchAsync(client, indexerId));

        Assert.Equal(2, tracker.Requests.Count(r => r.Url.AbsolutePath == "/login" && r.Method == "POST"));
        Assert.Equal(2, (await SessionRowAsync(indexerId)).ConsecutiveFailures);
    }

    [Fact]
    public async Task Only_a_search_that_gets_through_clears_the_failure_count()
    {
        var tracker = new TrackerHandler { SearchStatus = HttpStatusCode.Forbidden };
        var indexerId = await AddSessionIndexerAsync("Corrected");
        var client = NewClient(tracker);
        await Assert.ThrowsAnyAsync<Exception>(() => SearchAsync(client, indexerId));
        Assert.Equal(1, (await SessionRowAsync(indexerId)).ConsecutiveFailures);

        // The operator fixed the account on the site's side; the next attempt signs in and searches.
        await ExpireBackoffAsync(indexerId);
        tracker.SearchStatus = HttpStatusCode.OK;
        Assert.Single(await SearchAsync(client, indexerId));

        var row = await SessionRowAsync(indexerId);
        Assert.Equal(0, row.ConsecutiveFailures);
        Assert.NotNull(row.ConfirmedAt);
        Assert.True(row.LastAttemptOk);
    }

    [Fact]
    public async Task A_late_refusal_of_a_replaced_jar_is_not_charged_to_its_successor()
    {
        // Arrange — two searches shared a proven jar and the site expired it. The first one to hear
        // about it renewed the session; the second one's refusal arrives afterwards.
        var tracker = new TrackerHandler();
        var indexerId = await AddSessionIndexerAsync("Shared");
        var sessions = NewSessionManager(tracker);
        var baseUri = new Uri("https://tracker.example/");
        var shared = (await sessions.EnsureSessionAsync(indexerId.Value, "Shared", baseUri, SessionBlock(), Credential))!;
        await sessions.ConfirmAsync(indexerId.Value, shared);
        await sessions.ReportRejectedAsync(indexerId.Value, "Shared", shared);
        var renewed = (await sessions.EnsureSessionAsync(indexerId.Value, "Shared", baseUri, SessionBlock(), Credential))!;

        // Act
        await sessions.ReportRejectedAsync(indexerId.Value, "Shared", shared);

        // Assert — the renewed session is untouched and nothing counts against the credential.
        Assert.Same(renewed, await sessions.EnsureSessionAsync(indexerId.Value, "Shared", baseUri, SessionBlock(), Credential));
        var row = await SessionRowAsync(indexerId);
        Assert.Equal(0, row.ConsecutiveFailures);
        Assert.NotNull(row.CookiesCipher);
        Assert.Equal(2, tracker.Requests.Count(r => r.Url.AbsolutePath == "/login" && r.Method == "POST"));
    }

    [Fact]
    public async Task A_proven_session_is_still_proven_after_a_restart()
    {
        var tracker = new TrackerHandler();
        var indexerId = await AddSessionIndexerAsync("Remembered");
        await SearchAsync(NewClient(tracker), indexerId);

        var previous = _provider;
        _provider = await RebuildProviderAsync();
        await previous.DisposeAsync();

        // A restarted process reads the proof from the row, so the site expiring the session is
        // renewed at once and not held off as a failed sign-in.
        var restartedTracker = new TrackerHandler();
        restartedTracker.PlanSearch(HttpStatusCode.Forbidden);
        Assert.Single(await SearchAsync(NewClient(restartedTracker), indexerId));
        Assert.Equal(1, restartedTracker.Requests.Count(r => r.Url.AbsolutePath == "/login" && r.Method == "POST"));
        Assert.Equal(0, (await SessionRowAsync(indexerId)).ConsecutiveFailures);
    }

    [Fact]
    public async Task A_definition_with_no_usable_credential_is_searched_exactly_as_before()
    {
        var tracker = new TrackerHandler();
        var indexerId = await AddSessionIndexerAsync("Open");
        var sessions = NewSessionManager(tracker);
        var client = new DefinitionIndexerClient(
            new HttpClient(tracker), logger: null, sessionManager: sessions);

        // No credential resolved: there is nothing to sign in with, so no login request is issued
        // and the search runs unauthenticated — the pre-session behaviour, byte for byte.
        var candidates = await client.SearchAsync(
            Summary(indexerId), null, LoginDefinitionJson, new Cinomni.Search.Contracts.SearchCriterion("Interstellar", 2014, null, null, "Movie"));

        Assert.Single(candidates);
        Assert.DoesNotContain(tracker.Requests, r => r.Url.AbsolutePath == "/login");
        Assert.Null(Assert.Single(tracker.SearchRequests()).Cookie);
    }

    [Fact]
    public async Task A_test_whose_sign_in_did_not_work_fails_instead_of_passing_as_a_visitor()
    {
        // Arrange — the login lands back on the form: the search still runs, anonymously.
        var tracker = new TrackerHandler { LoginSucceeds = false };
        var indexerId = await AddSessionIndexerAsync("WrongPassword");
        await SetCredentialAsync(indexerId, "operator", "hunter2");

        // Act
        var result = await NewAdministration(tracker).TestIndexerAsync(indexerId);

        // Assert — a failed test that names the sign-in, not "succeeded" with a quiet flag beside it.
        Assert.True(result.IsSuccess, result.IsSuccess ? null : result.Error.Message);
        Assert.False(result.Value.Succeeded);
        Assert.False(result.Value.Authenticated);
        Assert.Equal(IndexerAdministration.NotSignedInCode, result.Value.Code);
    }

    [Fact]
    public async Task A_challenge_met_signing_in_without_flaresolverr_is_not_counted_as_a_wrong_password()
    {
        // Arrange — the site sits behind a challenge and FlareSolverr is still off, the order an operator
        // naturally sets things up in. Counted as a failed sign-in, it held off the next one for minutes
        // after FlareSolverr was turned on.
        var tracker = new TrackerHandler { RequiredClearance = ("solved-1", "Agent/1.0") };
        var indexerId = await AddSessionIndexerAsync("NotYetCleared");
        await SetCredentialAsync(indexerId, "operator", "hunter2");

        // Act
        var result = await NewAdministration(tracker).TestIndexerAsync(indexerId);

        // Assert — the test says what to do, and no backoff was started.
        Assert.False(result.Value.Succeeded);
        Assert.Equal("discovery.indexer.test_browser_challenge", result.Value.Code);
        var row = await SessionRowOrNullAsync(indexerId);
        Assert.True(row is null || row.ConsecutiveFailures == 0);
    }

    [Fact]
    public async Task A_credential_makes_the_indexer_test_search_authenticated()
    {
        var tracker = new TrackerHandler();
        var indexerId = await AddSessionIndexerAsync("Tested");
        await SetCredentialAsync(indexerId, "operator", "hunter2");

        var admin = NewAdministration(tracker);
        var result = await admin.TestIndexerAsync(indexerId);

        Assert.True(result.IsSuccess, result.IsSuccess ? null : result.Error.Message);
        Assert.True(result.Value.Authenticated);
        Assert.Equal(1, tracker.Requests.Count(r => r.Url.AbsolutePath == "/login" && r.Method == "POST"));
        Assert.All(
            tracker.SearchRequests(),
            search => Assert.Contains("session=abc123", search.Cookie!, StringComparison.Ordinal));

        // And the listing says what actually happened: a readable credential whose login worked is
        // an active session with a captured-at timestamp.
        var listed = Assert.Single(await admin.ListIndexersAsync(), i => i.Id == indexerId);
        Assert.Equal(IndexerSessionState.Active, listed.SessionState);
        Assert.NotNull(listed.LastLoginAt);
    }

    [Fact]
    public async Task The_listing_tells_the_session_states_apart()
    {
        var tracker = new TrackerHandler();
        var signedOut = await AddSessionIndexerAsync("SignedOut");
        var failed = await AddSessionIndexerAsync("Failed");
        var noLogin = await AddSessionIndexerAsync("NoLogin", DefinitionWithoutSessionJson);
        var plain = await AddTorznabIndexerAsync("Plain");
        // Declares a login and has nothing to sign in with: the case that makes 'None' ambiguous.
        var uncredentialed = await AddSessionIndexerAsync("Uncredentialed");

        // A credential that has never been used: the login is declared and readable, nothing ran yet.
        await SetCredentialAsync(signedOut, "operator", "hunter2");
        await SetCredentialAsync(failed, "operator", "hunter2");
        var sessions = NewSessionManager(tracker);
        var failedTracker = new TrackerHandler { LoginSucceeds = false };
        await NewSessionManager(failedTracker).EnsureSessionAsync(
            failed.Value, "Failed", new Uri("https://tracker.example/"), SessionBlock(), Credential);

        var administration = NewAdministration(tracker);
        var listed = await administration.ListIndexersAsync();

        Assert.Equal(
            IndexerSessionState.NotLoggedIn,
            Assert.Single(listed, i => i.Id == signedOut).SessionState);
        Assert.Equal(
            IndexerSessionState.Failed,
            Assert.Single(listed, i => i.Id == failed).SessionState);
        Assert.Equal(
            IndexerSessionState.None,
            Assert.Single(listed, i => i.Id == noLogin).SessionState);
        Assert.Equal(
            IndexerSessionState.None,
            Assert.Single(listed, i => i.Id == plain).SessionState);

        // 'None' is not "needs no login": an indexer that declares one and has no credential to use
        // reports it too. Which of the two it is, only DeclaresLogin says — so a caller deciding
        // whether to offer the FlareSolverr transport reads that and never infers it from the state.
        var withoutCredential = Assert.Single(listed, i => i.Id == uncredentialed);
        Assert.Equal(IndexerSessionState.None, withoutCredential.SessionState);
        Assert.True(withoutCredential.DeclaresLogin);
        Assert.True(Assert.Single(listed, i => i.Id == signedOut).DeclaresLogin);
        Assert.False(Assert.Single(listed, i => i.Id == noLogin).DeclaresLogin);
        Assert.False(Assert.Single(listed, i => i.Id == plain).DeclaresLogin);
    }

    [Fact]
    public async Task A_session_block_accepts_a_flaresolverr_setting()
    {
        // FlareSolverr no longer fetches the pages of a site that signs in: it solves the challenge
        // once and the session travels on plain requests carrying that clearance. The pair is exactly
        // what a private tracker behind a browser challenge needs, so it is no longer refused.
        var withSession = await AddSessionIndexerAsync("Both");
        var withoutSession = await AddSessionIndexerAsync("Either", DefinitionWithoutSessionJson);

        var administration = NewAdministration(new TrackerHandler());

        var both = await administration.SetSettingsAsync(
            withSession, new IndexerSettings(UseFlareSolverr: true));
        var plain = await administration.SetSettingsAsync(
            withoutSession, new IndexerSettings(UseFlareSolverr: true));

        Assert.True(both.IsSuccess, both.IsFailure ? both.Error.Message : null);
        Assert.True(plain.IsSuccess, plain.IsFailure ? plain.Error.Message : null);
    }

    [Fact]
    public async Task A_login_walled_site_behind_a_challenge_signs_in_and_searches_with_the_solved_clearance()
    {
        // Arrange — every request is challenged unless it carries the clearance and its agent.
        var tracker = new TrackerHandler { RequiredClearance = ("solved-1", "Agent/1.0") };
        var solver = new BrowserClearanceTests.CountingSolver();
        var indexerId = await AddSessionIndexerAsync("Cleared");

        // Act
        var results = await ClearedSearchAsync(NewClearedClient(tracker, solver), indexerId);

        // Assert — one solve; the sign-in and the search both went out as the browser that solved it,
        // and the search carried the session the sign-in captured next to the clearance.
        Assert.Single(results);
        Assert.Equal(1, solver.Solves);
        var submit = Assert.Single(tracker.Requests, r => r.Url.AbsolutePath == "/login" && r.Method == "POST");
        Assert.Equal("Agent/1.0", submit.UserAgent);
        Assert.Contains("cf_clearance=solved-1", submit.Cookie, StringComparison.Ordinal);
        var search = Assert.Single(tracker.SearchRequests());
        Assert.Contains("session=abc123", search.Cookie, StringComparison.Ordinal);
        Assert.Contains("cf_clearance=solved-1", search.Cookie, StringComparison.Ordinal);

        // The clearance is not a session: the jar kept encrypted at rest holds no challenge cookie.
        var sessions = NewSessionManager(tracker);
        var kept = await sessions.EnsureSessionAsync(
            indexerId.Value, "Cleared", new Uri("https://tracker.example/"), SessionBlock(), Credential);
        Assert.DoesNotContain(kept!, cookie => BrowserChallenge.IsClearanceCookie(cookie.Name));
    }

    [Fact]
    public async Task A_clearance_the_site_stops_honouring_is_solved_again_once_and_the_search_retried()
    {
        // Arrange — the first search meets a fresh challenge: the clearance lapsed mid-session.
        var tracker = new TrackerHandler { ChallengeNextSearches = 1 };
        var solver = new BrowserClearanceTests.CountingSolver();
        var indexerId = await AddSessionIndexerAsync("Relapsed");

        // Act
        var results = await ClearedSearchAsync(NewClearedClient(tracker, solver), indexerId);

        // Assert — solved twice, searched twice, and the retry went out on the renewed clearance.
        Assert.Single(results);
        Assert.Equal(2, solver.Solves);
        Assert.Equal(2, tracker.SearchRequests().Count);
        Assert.Contains("cf_clearance=solved-2", tracker.SearchRequests()[^1].Cookie, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_site_that_keeps_challenging_fails_the_search_instead_of_solving_for_ever()
    {
        // Arrange — the clearance the solver produces is never honoured.
        var tracker = new TrackerHandler { RequiredClearance = ("never", "Agent/1.0") };
        var solver = new BrowserClearanceTests.CountingSolver();
        var indexerId = await AddSessionIndexerAsync("Walled");

        // Act / Assert — a bounded number of solves, and a failure rather than an empty result.
        await Assert.ThrowsAnyAsync<Exception>(() => ClearedSearchAsync(NewClearedClient(tracker, solver), indexerId));
        Assert.InRange(solver.Solves, 1, 2);

        // A challenge is not a wrong password: it must not count against the sign-in's backoff.
        var row = await SessionRowOrNullAsync(indexerId);
        Assert.True(row is null || row.ConsecutiveFailures == 0);
    }

    [Fact]
    public async Task A_member_only_release_file_is_fetched_with_the_owning_indexers_session()
    {
        // Arrange
        var tracker = new TrackerHandler();
        var indexerId = await AddSessionIndexerAsync("Private");
        await SetCredentialAsync(indexerId, "operator", "hunter2");
        await RecordResultAsync(indexerId, "https://tracker.example/dl/1.torrent");

        // Act
        var file = await NewReleaseFileSource(tracker).FetchAsync("https://tracker.example/dl/1.torrent");

        // Assert — the file itself comes back, fetched as the member; nothing downstream sees a cookie.
        Assert.Equal(ReleaseFileOutcome.TorrentFile, file.Outcome);
        Assert.Equal(TrackerHandler.TorrentBytes, file.TorrentFile);
        var fetch = Assert.Single(tracker.Requests, r => r.Url.AbsolutePath == "/dl/1.torrent");
        Assert.Contains("session=abc123", fetch.Cookie, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_release_file_answered_with_the_login_form_signs_in_again_once_and_retries()
    {
        // Arrange — a session a search has already proven, which the site then expires. (A session
        // refused the moment it was created is a failed sign-in instead, and waits out its backoff.)
        var tracker = new TrackerHandler();
        var indexerId = await AddSessionIndexerAsync("Expiring");
        await SetCredentialAsync(indexerId, "operator", "hunter2");
        await RecordResultAsync(indexerId, "https://tracker.example/dl/1.torrent");
        var client = NewClient(tracker);
        await SearchAsync(client, indexerId);
        tracker.ExpireNextFileFetches = 1;

        // Act
        var file = await NewReleaseFileSource(client).FetchAsync("https://tracker.example/dl/1.torrent");

        Assert.Equal(ReleaseFileOutcome.TorrentFile, file.Outcome);
        Assert.Equal(2, tracker.Requests.Count(r => r.Url.AbsolutePath == "/dl/1.torrent"));
        Assert.Equal(2, tracker.Requests.Count(r => r.Url.AbsolutePath == "/login" && r.Method == "POST"));
    }

    [Fact]
    public async Task A_link_no_signed_in_indexer_owns_is_left_to_the_anonymous_fetch()
    {
        // Arrange — a login indexer on tracker.example; links elsewhere, a lookalike host, cleartext.
        var tracker = new TrackerHandler();
        var indexerId = await AddSessionIndexerAsync("Owner");
        await SetCredentialAsync(indexerId, "operator", "hunter2");
        var source = NewReleaseFileSource(tracker);

        // Act / Assert — none of these may carry the session anywhere.
        foreach (var link in new[]
                 {
                     "https://public.example/dl/1.torrent",
                     "https://tracker.example.evil/dl/1.torrent",
                     "http://tracker.example/dl/1.torrent",
                     "magnet:?xt=urn:btih:abc",
                 })
        {
            Assert.Equal(ReleaseFileOutcome.NotHandled, (await source.FetchAsync(link)).Outcome);
        }

        Assert.Empty(tracker.Requests);
    }

    [Fact]
    public async Task A_member_only_release_file_behind_a_challenge_is_fetched_with_the_clearance()
    {
        var tracker = new TrackerHandler { RequiredClearance = ("solved-1", "Agent/1.0") };
        var indexerId = await AddSessionIndexerAsync("ClearedFile");
        await SetCredentialAsync(indexerId, "operator", "hunter2");
        await RecordResultAsync(indexerId, "https://tracker.example/dl/1.torrent");
        await SetFlareSolverrAsync(indexerId);

        var file = await NewReleaseFileSource(tracker, new BrowserClearanceTests.CountingSolver())
            .FetchAsync("https://tracker.example/dl/1.torrent");

        Assert.Equal(ReleaseFileOutcome.TorrentFile, file.Outcome);
        var fetch = Assert.Single(tracker.Requests, r => r.Url.AbsolutePath == "/dl/1.torrent");
        Assert.Contains("cf_clearance=solved-1", fetch.Cookie, StringComparison.Ordinal);
        Assert.Equal("Agent/1.0", fetch.UserAgent);
    }

    [Fact]
    public async Task A_link_into_a_private_tracker_that_came_from_another_indexer_never_carries_its_session()
    {
        // Arrange — any indexer can return a link into the private tracker's origin. Fetched with the
        // member's session, that is a snatch on the member's account the tracker never offered, and
        // any state-changing GET the other indexer cares to point at.
        var tracker = new TrackerHandler();
        var indexerId = await AddSessionIndexerAsync("Private");
        await SetCredentialAsync(indexerId, "operator", "hunter2");
        await RecordResultAsync(new IndexerId(Guid.NewGuid()), "https://tracker.example/dl/9.torrent", "Some Public Indexer");

        // Act
        var file = await NewReleaseFileSource(tracker).FetchAsync("https://tracker.example/dl/9.torrent");

        // Assert
        Assert.Equal(ReleaseFileOutcome.NotHandled, file.Outcome);
        Assert.Empty(tracker.Requests);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_link_attributed_only_by_the_owners_name_never_carries_its_session(bool fromAnotherIndexer)
    {
        // Arrange — a result that carries the owner's name but not its id: another indexer the operator
        // gave the same name (or that took it after a rename), or a row stored before ids were recorded.
        // A name is a label; only the id says the owner offered the link.
        var tracker = new TrackerHandler();
        var indexerId = await AddSessionIndexerAsync("Private");
        await SetCredentialAsync(indexerId, "operator", "hunter2");
        await RecordResultAsync(
            fromAnotherIndexer ? new IndexerId(Guid.NewGuid()) : null, "https://tracker.example/dl/9.torrent");

        // Act
        var file = await NewReleaseFileSource(tracker).FetchAsync("https://tracker.example/dl/9.torrent");

        // Assert
        Assert.Equal(ReleaseFileOutcome.NotHandled, file.Outcome);
        Assert.Empty(tracker.Requests);
    }

    [Fact]
    public async Task A_disabled_private_indexer_lends_its_session_to_nothing()
    {
        var tracker = new TrackerHandler();
        var indexerId = await AddSessionIndexerAsync("Paused");
        await SetCredentialAsync(indexerId, "operator", "hunter2");
        await RecordResultAsync(indexerId, "https://tracker.example/dl/1.torrent");
        await using (var scope = _provider.CreateAsyncScope())
        {
            var disabled = await scope.ServiceProvider.GetRequiredService<IIndexerAdministration>()
                .SetEnabledAsync(indexerId, false);
            Assert.True(disabled.IsSuccess);
        }

        var file = await NewReleaseFileSource(tracker).FetchAsync("https://tracker.example/dl/1.torrent");

        Assert.Equal(ReleaseFileOutcome.NotHandled, file.Outcome);
        Assert.Empty(tracker.Requests);
    }

    // -- helpers ---------------------------------------------------------------------------------

    /// <summary>
    /// A search result as the search would have stored it, returned by <paramref name="returnedBy"/> — null
    /// for a row stored before results recorded their indexer.
    /// </summary>
    private async Task RecordResultAsync(IndexerId? returnedBy, string downloadUrl, string indexerName = "Private")
    {
        await using var scope = _provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<DiscoveryDbContext>();
        var now = DateTimeOffset.UtcNow;
        var execution = new SearchExecution
        {
            Id = Guid.NewGuid(), StartedAt = now, CompletedAt = now, Term = "Interstellar", ContentKind = "Movie",
        };
        db.SearchExecutions.Add(execution);
        db.SearchResults.Add(new SearchResult
        {
            Id = Guid.NewGuid(), ExecutionId = execution.Id, FoundAt = now, ReleaseGuid = downloadUrl,
            Title = "Interstellar 2014 1080p", DownloadUrl = downloadUrl, IndexerName = indexerName,
            IndexerId = returnedBy?.Value,
        });
        await db.SaveChangesAsync();
    }

    private ReleaseFileSource NewReleaseFileSource(TrackerHandler tracker, IIndexerClearanceSolver? solver = null) =>
        NewReleaseFileSource(solver is null ? NewClient(tracker) : NewClearedClient(tracker, solver));

    private ReleaseFileSource NewReleaseFileSource(DefinitionIndexerClient client)
    {
        var scope = _provider.CreateAsyncScope();
        return new ReleaseFileSource(
            scope.ServiceProvider.GetRequiredService<DiscoveryDbContext>(),
            scope.ServiceProvider.GetRequiredService<IndexerCredentialProtector>(),
            client);
    }

    private async Task SetFlareSolverrAsync(IndexerId indexerId)
    {
        await using var scope = _provider.CreateAsyncScope();
        var set = await scope.ServiceProvider.GetRequiredService<IIndexerAdministration>()
            .SetSettingsAsync(indexerId, new IndexerSettings(UseFlareSolverr: true));
        Assert.True(set.IsSuccess, set.IsFailure ? set.Error.Message : null);
    }

    private const string DefinitionWithoutSessionJson = """
        {
          "schemaVersion": 1,
          "resultKind": "Torrent",
          "search": {
            "requests": [{ "contentKinds": ["Movie"], "method": "Get", "urlTemplate": "/search?q={{term}}" }],
            "responseFormat": "Html",
            "rows": { "selector": "tr.result", "maxRows": 50 },
            "fields": {
              "title": { "selector": "td.name a", "attribute": "Text" },
              "downloadUrl": { "selector": "td.dl a", "attribute": "Href" }
            }
          }
        }
        """;

    private static DefinitionSession SessionBlock() =>
        IndexerDefinitionParser.Parse(LoginDefinitionJson).Value.Session!;

    private DefinitionIndexerClient NewClient(TrackerHandler tracker) =>
        new(new HttpClient(tracker), logger: null, sessionManager: NewSessionManager(tracker));

    /// <summary>
    /// The definition client wired the way a FlareSolverr-enabled login indexer runs: the clearance
    /// cache over a fake solver, and the proxied client (here, the fake tracker) the clearance is used on.
    /// </summary>
    private DefinitionIndexerClient NewClearedClient(TrackerHandler tracker, IIndexerClearanceSolver solver) =>
        new(
            new HttpClient(tracker),
            logger: null,
            sessionManager: NewSessionManager(tracker),
            clearance: new IndexerClearanceCache(() => solver, TimeProvider.System),
            httpClientFactory: new OneHandlerClientFactory(tracker));

    private static Task<IReadOnlyList<ReleaseCandidate>> ClearedSearchAsync(DefinitionIndexerClient client, IndexerId indexerId) =>
        client.SearchAsync(
            Summary(indexerId) with { Settings = new IndexerSettings(UseFlareSolverr: true) },
            Credential,
            LoginDefinitionJson,
            new Cinomni.Search.Contracts.SearchCriterion("Interstellar", 2014, null, null, "Movie"));

    private async Task<IndexerSession?> SessionRowOrNullAsync(IndexerId indexerId)
    {
        await using var scope = _provider.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<DiscoveryDbContext>().IndexerSessions
            .AsNoTracking()
            .SingleOrDefaultAsync(s => s.IndexerId == indexerId.Value);
    }

    private static Task<IReadOnlyList<ReleaseCandidate>> SearchAsync(DefinitionIndexerClient client, IndexerId indexerId) =>
        client.SearchAsync(
            Summary(indexerId), Credential, LoginDefinitionJson,
            new Cinomni.Search.Contracts.SearchCriterion("Interstellar", 2014, null, null, "Movie"));

    private async Task<IndexerSession> SessionRowAsync(IndexerId indexerId)
    {
        await using var scope = _provider.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<DiscoveryDbContext>().IndexerSessions
            .AsNoTracking()
            .SingleAsync(s => s.IndexerId == indexerId.Value);
    }

    /// <summary>Moves the last attempt a day into the past, which is longer than any backoff window.</summary>
    private async Task ExpireBackoffAsync(IndexerId indexerId)
    {
        await using var scope = _provider.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<DiscoveryDbContext>().Database.ExecuteSqlAsync(
            $"UPDATE discovery.indexer_sessions SET last_attempt_at = last_attempt_at - interval '1 day' WHERE indexer_id = {indexerId.Value}");
    }

    private IndexerSessionManager NewSessionManager(TrackerHandler tracker) => new(
        new OneHandlerClientFactory(tracker),
        _provider.GetRequiredService<IServiceScopeFactory>(),
        _provider.GetRequiredService<IndexerCredentialProtector>());

    /// <summary>
    /// The factory the manager resolves its client from, over the fake tracker. It is a factory and
    /// not a client because the manager asks for one per sign-in: a client captured for the life of
    /// the process would pin one handler past its rotation window and never re-resolve DNS.
    /// </summary>
    private sealed class OneHandlerClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    /// <summary>
    /// The real administration surface with the real definition client and session manager behind
    /// it, pointed at the fake tracker — so a test that asks it to test or list an indexer exercises
    /// the same sign-in path a search does.
    /// </summary>
    private IndexerAdministration NewAdministration(TrackerHandler tracker)
    {
        var sessions = NewSessionManager(tracker);
        var scope = _provider.CreateAsyncScope();
        return new IndexerAdministration(
            scope.ServiceProvider.GetRequiredService<DiscoveryDbContext>(),
            scope.ServiceProvider.GetRequiredService<IUnitOfWork>(),
            scope.ServiceProvider.GetRequiredService<IndexerCredentialProtector>(),
            new DefinitionIndexerClient(new HttpClient(tracker), sessionManager: sessions),
            sessions);
    }

    private async Task<IndexerId> AddSessionIndexerAsync(string name, string? definitionJson = null)
    {
        await using var scope = _provider.CreateAsyncScope();
        var admin = scope.ServiceProvider.GetRequiredService<IIndexerAdministration>();
        var uploaded = await admin.UploadDefinitionAsync(name, definitionJson ?? LoginDefinitionJson);
        Assert.True(uploaded.IsSuccess, uploaded.IsFailure ? uploaded.Error.Message : null);
        var added = await admin.AddIndexerAsync(
            name, IndexerProtocol.Definition, "https://tracker.example", 1, uploaded.Value);
        Assert.True(added.IsSuccess, added.IsFailure ? added.Error.Message : null);
        return added.Value;
    }

    private async Task<IndexerId> AddTorznabIndexerAsync(string name)
    {
        await using var scope = _provider.CreateAsyncScope();
        var admin = scope.ServiceProvider.GetRequiredService<IIndexerAdministration>();
        var added = await admin.AddIndexerAsync(name, IndexerProtocol.Torznab, "https://tracker.example/api", 2);
        Assert.True(added.IsSuccess, added.IsFailure ? added.Error.Message : null);
        return added.Value;
    }

    private async Task SetCredentialAsync(IndexerId indexerId, string username, string secret)
    {
        await using var scope = _provider.CreateAsyncScope();
        var admin = scope.ServiceProvider.GetRequiredService<IIndexerAdministration>();
        var stored = await admin.SetCredentialAsync(indexerId, username, secret);
        Assert.True(stored.IsSuccess, stored.IsFailure ? stored.Error.Message : null);
    }

    /// <summary>
    /// The summary the adapter is handed for a search. It names the real stored indexer, because the
    /// session row is keyed by that id — a made-up one has no indexer to belong to and could never be
    /// persisted, which would quietly turn every one of these into an unauthenticated search.
    /// </summary>
    private static IndexerSummary Summary(IndexerId indexerId) => new(
        indexerId, "Tracker", IndexerProtocol.Definition, "https://tracker.example/", 1, true,
        DefinitionId: new IndexerDefinitionId(Guid.NewGuid()));

    /// <summary>
    /// A fresh provider over the same database, the way a process restart finds it — nothing is
    /// deleted, and the schema is already at head.
    /// </summary>
    private async Task<ServiceProvider> RebuildProviderAsync()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddOperations(DiscoveryTestHost.ConnectionStringFor(Database));
        services.AddDiscoveryModule();
        var provider = services.BuildServiceProvider();
        await provider.MigrateDiscoveryAsync();
        return provider;
    }

    /// <summary>A fake tracker: a login page with a CSRF token, a form submit that sets the session cookie, and searches.</summary>
    internal sealed class TrackerHandler : HttpMessageHandler
    {
        public sealed record RecordedRequest(Uri Url, string Method, string? Cookie, string? Body, string? UserAgent = null);

        public List<RecordedRequest> Requests { get; } = [];

        /// <summary>
        /// When set, the site sits behind a browser challenge: a request without this clearance cookie
        /// value and the agent that solved it is answered with the interstitial, never with the page.
        /// </summary>
        public (string CookieValue, string Agent)? RequiredClearance { get; set; }

        /// <summary>How many of the next searches meet a fresh challenge anyway — the clearance lapsed mid-session.</summary>
        public int ChallengeNextSearches { get; set; }

        /// <summary>How many of the next release-file fetches find the session expired.</summary>
        public int ExpireNextFileFetches { get; set; }

        /// <summary>A minimal bencoded torrent: a dictionary holding an info dictionary.</summary>
        public static byte[] TorrentBytes { get; } =
            Encoding.ASCII.GetBytes("d8:announce21:https://t.example/ann4:infod6:lengthi1e4:name1:a12:piece lengthi16384e6:pieces0:ee");

        private readonly Queue<(HttpStatusCode Status, string Body, string? Location)> _plannedSearches = new();

        public HttpStatusCode SearchStatus { get; set; } = HttpStatusCode.OK;

        public bool LoginSucceeds { get; set; } = true;

        public void PlanSearch(HttpStatusCode status, string? body = null) =>
            _plannedSearches.Enqueue((status, body ?? SearchHtml, null));

        /// <summary>The commonest way a site says the session is gone: bounce the search to the login page.</summary>
        public void PlanSearchRedirectToLogin() =>
            _plannedSearches.Enqueue((HttpStatusCode.Found, string.Empty, "/login"));

        public IReadOnlyList<RecordedRequest> SearchRequests() =>
            Requests.Where(r => r.Url.AbsolutePath.StartsWith("/search", StringComparison.Ordinal)).ToList();

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            var cookie = request.Headers.TryGetValues("Cookie", out var cookies) ? string.Join("; ", cookies) : null;
            var agent = request.Headers.TryGetValues("User-Agent", out var agents) ? string.Join(" ", agents) : null;
            Requests.Add(new RecordedRequest(request.RequestUri!, request.Method.Method, cookie, body, agent));

            var searching = request.RequestUri!.AbsolutePath.StartsWith("/search", StringComparison.Ordinal);
            var cleared = RequiredClearance is not { } required
                || (cookie?.Contains($"cf_clearance={required.CookieValue}", StringComparison.Ordinal) == true
                    && agent == required.Agent);
            if (!cleared || (searching && ChallengeNextSearches-- > 0))
            {
                var challenge = Html(HttpStatusCode.Forbidden, "<html><title>Just a moment...</title></html>");
                challenge.Headers.TryAddWithoutValidation("cf-mitigated", "challenge");
                return challenge;
            }

            if (request.RequestUri!.AbsolutePath == "/login" && request.Method == HttpMethod.Post)
            {
                if (!LoginSucceeds)
                {
                    // The sign-in "worked" at the transport level but landed back on the login form:
                    // exactly what a wrong password looks like.
                    return Html(HttpStatusCode.OK, LoginHtml);
                }

                var redirect = new HttpResponseMessage(HttpStatusCode.Found);
                redirect.Headers.Add("Set-Cookie", "session=abc123; Path=/");
                if (RequiredClearance is not null)
                {
                    // The challenge provider refreshes its own cookie on any response it fronts.
                    redirect.Headers.Add("Set-Cookie", "__cf_bm=refreshed; Path=/");
                }

                return redirect;
            }

            if (request.RequestUri!.AbsolutePath.StartsWith("/dl/", StringComparison.Ordinal))
            {
                // A member-only file: served to the session, and the login form to anyone else.
                var signedIn = cookie?.Contains("session=abc123", StringComparison.Ordinal) == true
                    && ExpireNextFileFetches-- <= 0;
                return signedIn
                    ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(TorrentBytes) }
                    : Html(HttpStatusCode.OK, LoginHtml);
            }

            if (request.RequestUri!.AbsolutePath == "/login")
            {
                var page = Html(HttpStatusCode.OK, LoginHtml);
                page.Headers.Add("Set-Cookie", "pre=1; Path=/");
                return page;
            }

            return _plannedSearches.Count > 0
                ? Html(_plannedSearches.Dequeue())
                : Html(SearchStatus, SearchHtml);
        }

        private static HttpResponseMessage Html(HttpStatusCode status, string body) =>
            new(status) { Content = new StringContent(body, Encoding.UTF8, "text/html") };

        private static HttpResponseMessage Html((HttpStatusCode Status, string Body, string? Location) planned)
        {
            var response = Html(planned.Status, planned.Body);
            if (planned.Location is not null)
            {
                response.Headers.Location = new Uri(planned.Location, UriKind.Relative);
            }

            return response;
        }
    }
}
