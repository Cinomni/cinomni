using System.Security.Claims;
using System.Text.Encodings.Web;
using Cinomni.Kernel.Security;
using Cinomni.RealTime.Api;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Cinomni.RealTime.Tests;

/// <summary>
/// An open stream re-authenticates as if the request had just arrived. The request's own result is
/// cached for its whole life, which is how a stream used to outlive logout, revocation, a disabled
/// account and a demotion — and go on receiving operator progress.
/// </summary>
public sealed class RealtimeRevalidationTests
{
    private static readonly Guid UserId = Guid.NewGuid();

    [Fact]
    public async Task A_stream_whose_session_still_stands_goes_on()
    {
        var (context, account) = NewRequest();

        Assert.True(await RealtimeEndpoints.StillEntitledAsync(context, account.Viewer));
    }

    [Fact]
    public async Task A_stream_ends_once_its_session_is_gone()
    {
        // Logout, revocation, expiry and a disabled account all look like this to the stream.
        var (context, account) = NewRequest();
        var reader = account.Viewer;

        account.SignedIn = false;

        Assert.False(await RealtimeEndpoints.StillEntitledAsync(context, reader));
    }

    [Fact]
    public async Task A_stream_ends_when_its_reader_is_no_longer_who_opened_it()
    {
        // A demoted operator must stop receiving operator signals; reconnecting admits them as a member.
        var (context, account) = NewRequest();
        var reader = account.Viewer;

        account.IsAdministrator = false;

        Assert.False(await RealtimeEndpoints.StillEntitledAsync(context, reader));
    }

    private static (HttpContext Context, AccountState Account) NewRequest()
    {
        var account = new AccountState();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(account);
        services.AddAuthentication(TestScheme.Name).AddScheme<AuthenticationSchemeOptions, TestScheme>(TestScheme.Name, null);
        var context = new DefaultHttpContext { RequestServices = services.BuildServiceProvider() };
        return (context, account);
    }

    /// <summary>What the account looks like right now; the tests change it under an open stream.</summary>
    private sealed class AccountState
    {
        public bool SignedIn { get; set; } = true;

        public bool IsAdministrator { get; set; } = true;

        public Viewer Viewer => new(UserId, IsAdministrator);
    }

    /// <summary>Answers from <see cref="AccountState"/> each time it is asked, like a token checked against the database.</summary>
    private sealed class TestScheme(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder,
        AccountState account)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        public const string Name = "Test";

        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            if (!account.SignedIn)
            {
                return Task.FromResult(AuthenticateResult.Fail("signed out"));
            }

            var identity = new ClaimsIdentity(
                [
                    new Claim(ClaimTypes.NameIdentifier, UserId.ToString()),
                    new Claim(AuthorizationClaims.Administrator, account.IsAdministrator ? "true" : "false"),
                ],
                Name);
            return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), Name)));
        }
    }
}
