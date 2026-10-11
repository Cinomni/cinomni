using Cinomni.Identity.Contracts;
using Cinomni.Identity.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Cinomni.Identity.Application;

/// <summary>
/// Ends every session an account holds except the one making the change. Used wherever what protects
/// the account changes — its password, its second factor — because a session opened elsewhere under the
/// old protection should not outlive it. Only marks the rows; the caller's unit of work commits them.
/// </summary>
internal static class SessionRevocation
{
    public static async Task RevokeOtherSessionsAsync(
        this IdentityDbContext dbContext,
        UserId userId,
        string currentTokenHash,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var others = await dbContext.Sessions
            .Where(s => s.UserId == userId.Value && s.RevokedAt == null && s.TokenHash != currentTokenHash)
            .ToListAsync(cancellationToken);
        foreach (var session in others)
        {
            session.RevokedAt = now;
        }
    }
}
