using Cinomni.Kernel.Identifiers;
using Cinomni.Kernel.Results;

namespace Cinomni.Identity.Contracts;

/// <summary>Strongly-typed user identity (internal UUIDv7).</summary>
public readonly record struct UserId(Guid Value)
{
    public static UserId New() => new(Uuid7.New());
    public override string ToString() => Value.ToString();
}

/// <summary>
/// What an account is allowed to be. Deliberately two values: an administrator operates the
/// installation, a member consumes it. Anything finer is expressed as a permission, not as a new role,
/// so the authorization model stays one policy plus a handful of explicit capabilities.
/// </summary>
public enum UserRole
{
    Member = 1,
    Administrator = 2,
}

/// <summary>
/// What a member may do beyond browsing and watching. Administrators are not constrained by these —
/// they can already do everything the permissions describe.
/// </summary>
/// <param name="CanRequest">May submit requests. Off turns the account into a viewer.</param>
/// <param name="RequestsAutoApproved">Their requests are approved on submission, without an operator.</param>
/// <param name="OpenRequestLimit">
/// How many requests this account may have open at once — pending, or approved and not yet playable.
/// <c>null</c> defers to the installation default; <c>0</c> means no limit at all.
/// <para>
/// Identity owns the number and Requests owns the counting, which is the split that keeps "what this
/// account may do" in one place while the rows stay with the module that writes them.
/// </para>
/// </param>
public sealed record UserPermissions(
    bool CanRequest,
    bool RequestsAutoApproved,
    int? OpenRequestLimit = null,
    string? ContentCeiling = null,
    string? ContentCeilingRegion = null)
{
    /// <summary>What an invited member gets unless the administrator says otherwise.</summary>
    public static readonly UserPermissions Default =
        new(CanRequest: true, RequestsAutoApproved: false, OpenRequestLimit: null);

    /// <summary>
    /// What an administrator effectively has: every capability, unconditionally — including no cap on
    /// open requests, since theirs are approved on submission and never queue behind a decision.
    /// </summary>
    public static readonly UserPermissions Full =
        new(CanRequest: true, RequestsAutoApproved: true, OpenRequestLimit: 0);
}

/// <summary>Authenticated user (minimal projection exposed to other modules).</summary>
public sealed record AuthenticatedUser(UserId Id, string Username, UserRole Role, UserPermissions Permissions)
{
    public bool IsAdministrator => Role == UserRole.Administrator;
}

/// <summary>A local account as listed by an administrator (never carries credentials).</summary>
public sealed record UserAccount(
    UserId Id,
    string Username,
    UserRole Role,
    UserPermissions Permissions,
    bool IsDisabled,
    DateTimeOffset CreatedAt,
    DateTimeOffset? LastLoginAt)
{
    public bool IsAdministrator => Role == UserRole.Administrator;
}

/// <summary>
/// Public interface of the Identity module for authenticating local credentials
/// (passwords stored with a strong hash, never in plain text).
/// </summary>
public interface IAuthenticator
{
    /// <summary>
    /// Verifies a username and password and nothing else. Deliberately free of side effects: with a
    /// second factor in play, a correct password is not yet a sign-in, and recording one here would
    /// mark an account as having signed in when it may still fail the next step.
    /// </summary>
    Task<Result<AuthenticatedUser>> AuthenticateAsync(
        string username,
        string password,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Records that this account has signed in: the last-login timestamp and the event that announces
    /// it. Separate from <see cref="AuthenticateAsync"/> precisely so it happens once, at the end of
    /// whichever sequence that account actually has to complete.
    /// </summary>
    Task CompleteSignInAsync(UserId userId, CancellationToken cancellationToken = default);
}
