using Cinomni.Identity.Application;
using Cinomni.Identity.Contracts;
using Cinomni.Identity.Messaging;
using Cinomni.Identity.Persistence;
using Cinomni.Kernel.Identifiers;
using Cinomni.Kernel.Messaging;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Cinomni.Identity.Tests;

/// <summary>
/// Integration tests for the session retention sweep against a real PostgreSQL instance. This one is
/// security-positive before it is about size: a dead session row still holds the hash of a bearer
/// token, and residue that can authenticate nothing has no reason to survive. A live session must
/// never be touched — the sweep can never sign anybody out.
/// </summary>
public sealed class SessionRetentionTests : IAsyncLifetime
{
    private static readonly TimeSpan Grace = TimeSpan.FromDays(30);

    private ServiceProvider _provider = null!;
    private Guid _userId;

    public async Task InitializeAsync()
    {
        _provider = await IdentityTestHost.CreateAsync("cinomni_test_identity_retention");

        await using var scope = _provider.CreateAsyncScope();
        var provisioning = scope.ServiceProvider.GetRequiredService<IUserProvisioning>();
        var created = await provisioning.CreateAdminAsync("Admin", "correct-horse-battery");
        Assert.True(created.IsSuccess);
        _userId = created.Value.Value;
    }

    public async Task DisposeAsync() => await _provider.DisposeAsync();

    [Fact]
    public async Task Only_sessions_that_can_no_longer_authenticate_are_removed()
    {
        var now = DateTimeOffset.UtcNow;

        var longExpired = await SeedAsync(expiresAt: now - Grace - TimeSpan.FromDays(1), revokedAt: null);
        var longRevoked = await SeedAsync(
            expiresAt: now + TimeSpan.FromDays(365), revokedAt: now - Grace - TimeSpan.FromDays(1));
        var recentlyExpired = await SeedAsync(expiresAt: now - TimeSpan.FromDays(1), revokedAt: null);
        var live = await SeedAsync(expiresAt: now + TimeSpan.FromDays(29), revokedAt: null);

        await PurgeAsync();

        await using var scope = _provider.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<IdentityDbContext>();

        Assert.False(await dbContext.Sessions.AnyAsync(s => s.Id == longExpired));
        Assert.False(await dbContext.Sessions.AnyAsync(s => s.Id == longRevoked));
        Assert.True(await dbContext.Sessions.AnyAsync(s => s.Id == recentlyExpired));
        Assert.True(await dbContext.Sessions.AnyAsync(s => s.Id == live));
    }

    [Fact]
    public async Task A_live_session_still_authenticates_after_the_sweep()
    {
        string token;
        await using (var scope = _provider.CreateAsyncScope())
        {
            var sessions = scope.ServiceProvider.GetRequiredService<ISessionService>();
            token = (await sessions.IssueAsync(new UserId(_userId))).Token;
        }

        await SeedAsync(
            expiresAt: DateTimeOffset.UtcNow - Grace - TimeSpan.FromDays(5), revokedAt: null);

        await PurgeAsync();
        await PurgeAsync();

        await using (var scope = _provider.CreateAsyncScope())
        {
            var sessions = scope.ServiceProvider.GetRequiredService<ISessionService>();
            Assert.True((await sessions.ValidateAsync(token)).IsSuccess);

            var dbContext = scope.ServiceProvider.GetRequiredService<IdentityDbContext>();
            Assert.Equal(1, await dbContext.Sessions.CountAsync());
        }
    }

    private async Task PurgeAsync()
    {
        await using var scope = _provider.CreateAsyncScope();
        var handler = scope.ServiceProvider.GetRequiredService<ICommandHandler<PurgeSessionsCommand>>();
        Assert.True((await handler.HandleAsync(new PurgeSessionsCommand())).IsSuccess);
    }

    private async Task<Guid> SeedAsync(DateTimeOffset expiresAt, DateTimeOffset? revokedAt)
    {
        await using var scope = _provider.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<IdentityDbContext>();

        var session = new Session
        {
            Id = Uuid7.New(),
            UserId = _userId,
            // A synthetic hash-shaped value: no real token is involved, and none is logged.
            TokenHash = Convert.ToHexString(Guid.NewGuid().ToByteArray()),
            CreatedAt = expiresAt - TimeSpan.FromDays(30),
            ExpiresAt = expiresAt,
            LastActivityAt = expiresAt - TimeSpan.FromDays(30),
            RevokedAt = revokedAt,
        };

        dbContext.Sessions.Add(session);
        await dbContext.SaveChangesAsync();
        return session.Id;
    }
}
