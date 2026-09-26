namespace Cinomni.Kernel.Security;

/// <summary>
/// Names of the authorization policies the platform registers. They live in the kernel because every
/// module's endpoints gate on them, and a module must not depend on Identity to say "operators only"
/// (the same reason <c>SsrfGuard</c> is here: a rule shared by more than two modules belongs to the kernel).
/// Identity is what registers them and puts the backing claims on the principal.
/// </summary>
public static class AuthorizationPolicies
{
    /// <summary>
    /// The caller administers this installation. Gates every operator surface: adding titles, indexers,
    /// download control, imports, delivery channels and accounts. Regular accounts request instead.
    /// </summary>
    public const string Administrator = "identity.administrator";

    /// <summary>
    /// The caller may ask for titles: a member with the permission, or any administrator. An account
    /// without it can still browse and watch — it just cannot add to the queue of decisions.
    /// </summary>
    public const string CanRequest = "identity.can-request";
}

/// <summary>
/// Claims Identity puts on an authenticated principal, beyond the standard name and id. They are rebuilt
/// from the account on every request (the bearer token is validated against the database each time), so
/// revoking a permission takes effect immediately — there is no stale-session window.
/// </summary>
public static class AuthorizationClaims
{
    /// <summary>"true" when the session belongs to an administrator.</summary>
    public const string Administrator = "is_admin";

    /// <summary>"true" when the account may submit requests.</summary>
    public const string CanRequest = "can_request";

    /// <summary>"true" when the account's requests are approved on submission, without an operator.</summary>
    public const string RequestsAutoApproved = "requests_auto_approved";

    /// <summary>
    /// How many requests this account may have open at once. Absent means the installation default
    /// applies; "0" means no limit. Carried as a claim like the rest, so it is rebuilt from the
    /// account on every request and a change takes effect without waiting for a session to end.
    /// </summary>
    public const string OpenRequestLimit = "open_request_limit";

    /// <summary>
    /// The highest age classification this account may see, in <see cref="ContentCeilingRegion"/>'s
    /// ladder. Absent means no ceiling. Rebuilt from the account on every request, like the rest.
    /// </summary>
    public const string ContentCeiling = "content_ceiling";

    /// <summary>The region <see cref="ContentCeiling"/> was set against. Absent together with the ceiling.</summary>
    public const string ContentCeilingRegion = "content_ceiling_region";
}
