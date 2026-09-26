using Cinomni.Catalog.Contracts;
using Cinomni.Identity.Contracts;
using Cinomni.Identity.Events;
using Cinomni.Identity.Persistence;
using Cinomni.Identity.Security;
using Cinomni.Kernel.Identifiers;
using Cinomni.Kernel.Results;
using Cinomni.Metadata.Contracts;
using Cinomni.Operations.Messaging;
using Cinomni.Operations.Transactions;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Cinomni.Identity.Application;

/// <summary>
/// Creates and administers user accounts, including the first-run administrator. Every change that could
/// leave the installation unmanageable is refused: there is always at least one administrator who can
/// still sign in.
/// </summary>
public interface IUserProvisioning
{
    Task<bool> AnyUserExistsAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Creates the initial administrator. Fails if any account already exists, so first-run
    /// setup can only happen once.
    /// </summary>
    Task<Result<UserId>> CreateAdminAsync(string username, string password, CancellationToken cancellationToken = default);

    /// <summary>
    /// Creates a further account (an administrator invites the household). Fails if the username is
    /// taken — the unique index is the backstop.
    /// </summary>
    Task<Result<UserId>> CreateUserAsync(
        string username,
        string password,
        UserRole role,
        UserPermissions? permissions = null,
        CancellationToken cancellationToken = default);

    /// <summary>Lists the accounts, oldest first. Never exposes credentials.</summary>
    Task<IReadOnlyList<UserAccount>> ListAsync(CancellationToken cancellationToken = default);

    /// <summary>Promotes or demotes an account. Refuses to demote the last administrator who can sign in.</summary>
    Task<Result> SetRoleAsync(UserId id, UserRole role, CancellationToken cancellationToken = default);

    /// <summary>
    /// Replaces a member's permissions. An administrator's are implicit, so setting them is refused
    /// rather than silently ignored.
    /// </summary>
    Task<Result> SetPermissionsAsync(UserId id, UserPermissions permissions, CancellationToken cancellationToken = default);

    /// <summary>
    /// Disables or re-enables an account: a disabled one cannot authenticate and its sessions stop
    /// validating. Refuses to disable the last administrator who can sign in.
    /// </summary>
    Task<Result> SetDisabledAsync(UserId id, bool disabled, CancellationToken cancellationToken = default);
}

public sealed class UserProvisioning(
    IdentityDbContext dbContext,
    IPasswordHasher passwordHasher,
    IUnitOfWork unitOfWork,
    IEventBus eventBus,
    FirstRunState firstRun,
    IContentRatingRegion contentRatingRegion)
    : IUserProvisioning
{
    private static readonly Error NotFound = new("identity.user_not_found", "No such account.");

    private static readonly Error SetupClosed = new(
        "identity.setup_already_completed", "An account already exists; first-run setup is closed.");

    /// <summary>
    /// A number the wire accepted for an enum is not a role: <c>"role": 7</c> used to be stored, and an
    /// account whose role is neither administrator nor member is one no rule in the product was written for.
    /// </summary>
    private static readonly Error UnknownRole = new("identity.invalid_role", "The role must be Administrator or Member.");

    /// <summary>The same ceiling the installation-wide default has, so the two can be set alike.</summary>
    internal const int MaximumOpenRequestLimit = 1_000;

    private static readonly Error LastAdministrator = new(
        "identity.last_administrator", "There must be at least one administrator who can sign in.");

    public async Task<bool> AnyUserExistsAsync(CancellationToken cancellationToken = default)
    {
        if (firstRun.AnyUserHasExisted)
        {
            return true;
        }

        // Only the false answer costs a query, and only until first-run setup completes.
        var exists = await dbContext.Users.AnyAsync(cancellationToken);
        if (exists)
        {
            firstRun.RecordUserExists();
        }

        return exists;
    }

    public async Task<Result<UserId>> CreateAdminAsync(
        string username,
        string password,
        CancellationToken cancellationToken = default)
    {
        var credentials = Validate(username, password);
        if (credentials.IsFailure)
        {
            return Result<UserId>.Failure(credentials.Error);
        }

        if (await dbContext.Users.AnyAsync(cancellationToken))
        {
            return Result<UserId>.Failure(SetupClosed);
        }

        // Checked again inside the write, under the accounts lock: two setup requests racing each saw an
        // empty table here, and both created an administrator.
        return await PersistAsync(
            credentials.Value, password, UserRole.Administrator, UserPermissions.Full, cancellationToken, firstRunOnly: true);
    }

    public async Task<Result<UserId>> CreateUserAsync(
        string username,
        string password,
        UserRole role,
        UserPermissions? permissions = null,
        CancellationToken cancellationToken = default)
    {
        var credentials = Validate(username, password);
        if (credentials.IsFailure)
        {
            return Result<UserId>.Failure(credentials.Error);
        }

        if (await dbContext.Users.AnyAsync(u => u.Username == credentials.Value, cancellationToken))
        {
            return Result<UserId>.Failure(new Error("identity.username_taken", "That username is already taken."));
        }

        return await PersistAsync(
            credentials.Value, password, role, permissions ?? UserPermissions.Default, cancellationToken);
    }

    public async Task<IReadOnlyList<UserAccount>> ListAsync(CancellationToken cancellationToken = default)
    {
        var users = await dbContext.Users
            .AsNoTracking()
            .OrderBy(u => u.CreatedAt)
            .ToListAsync(cancellationToken);

        return users.Select(u => u.ToAccount()).ToList();
    }

    public async Task<Result> SetRoleAsync(UserId id, UserRole role, CancellationToken cancellationToken = default)
    {
        if (!Enum.IsDefined(role))
        {
            return Result.Failure(UnknownRole);
        }

        return await ChangeAccountAsync(id, cancellationToken, async (user, token) =>
        {
            if (user.Role == role)
            {
                return Result.Success();
            }

            // Demoting the only administrator who can still sign in would lock everyone out of the
            // operator surfaces, with no way back in through the API.
            if (role != UserRole.Administrator && await IsLastActiveAdministratorAsync(user, token))
            {
                return Result.Failure(LastAdministrator);
            }

            user.Role = role;
            return Result.Success();
        });
    }

    public async Task<Result> SetPermissionsAsync(
        UserId id,
        UserPermissions permissions,
        CancellationToken cancellationToken = default)
    {
        var user = await dbContext.Users.FirstOrDefaultAsync(u => u.Id == id.Value, cancellationToken);
        if (user is null)
        {
            return Result.Failure(NotFound);
        }

        if (user.IsAdministrator)
        {
            return Result.Failure(new Error(
                "identity.administrator_permissions", "An administrator already has every permission."));
        }

        if (InvalidLimit(permissions) is { } invalidLimit)
        {
            return Result.Failure(invalidLimit);
        }

        // The ceiling the account already has comes back with every other permission change: the form
        // sends the whole set. Re-validated against today's region it would fail once the installation
        // classifies differently, or quietly re-stamp a restriction that had stopped applying. Unchanged
        // means unchanged, region included; only a different ceiling is a decision to check.
        var normalized = IsStoredCeiling(user, permissions.ContentCeiling)
            ? Result<UserPermissions>.Success(permissions with
            {
                ContentCeiling = user.ContentCeiling,
                ContentCeilingRegion = user.ContentCeilingRegion,
            })
            : NormalizeCeiling(permissions);
        if (normalized.IsFailure)
        {
            return Result.Failure(normalized.Error);
        }

        user.CanRequest = normalized.Value.CanRequest;
        user.RequestsAutoApproved = normalized.Value.RequestsAutoApproved;
        user.OpenRequestLimit = normalized.Value.OpenRequestLimit;
        user.ContentCeiling = normalized.Value.ContentCeiling;
        user.ContentCeilingRegion = normalized.Value.ContentCeilingRegion;
        await SaveAsync(cancellationToken);
        return Result.Success();
    }

    public Task<Result> SetDisabledAsync(UserId id, bool disabled, CancellationToken cancellationToken = default) =>
        ChangeAccountAsync(id, cancellationToken, async (user, token) =>
        {
            if (user.IsDisabled == disabled)
            {
                return Result.Success();
            }

            if (disabled && await IsLastActiveAdministratorAsync(user, token))
            {
                return Result.Failure(LastAdministrator);
            }

            user.IsDisabled = disabled;
            return Result.Success();
        });

    /// <summary>
    /// Reads one account, lets <paramref name="change"/> decide and modify it, and saves — all while
    /// holding <see cref="AccountsLockKey"/>. The last-administrator rule is a count of other rows, which
    /// no constraint can express: two demotions checked side by side each saw the other administrator
    /// still standing, both committed, and none was left. Under the lock the second one counts after
    /// the first has committed.
    /// </summary>
    private async Task<Result> ChangeAccountAsync(
        UserId id, CancellationToken cancellationToken, Func<User, CancellationToken, Task<Result>> change)
    {
        var result = Result.Failure(NotFound);
        await unitOfWork.ExecuteAsync(async token =>
        {
            await LockAccountsAsync(token);
            // Read fresh under the lock: a copy tracked before it would carry what the other request changed.
            var user = await dbContext.Users.FirstOrDefaultAsync(u => u.Id == id.Value, token);
            if (user is null)
            {
                return;
            }

            await dbContext.Entry(user).ReloadAsync(token);
            result = await change(user, token);
            if (result.IsSuccess)
            {
                await dbContext.SaveChangesAsync(token);
            }
        }, cancellationToken);

        return result;
    }

    /// <summary>
    /// Serializes the changes whose rule spans several accounts: first-run setup and taking away an
    /// administrator. Held until the transaction ends, so it cannot outlive a crash; everything else
    /// about an account is a single-row write that needs no lock.
    /// </summary>
    private const long AccountsLockKey = 6_113_552_419_101L;

    private Task LockAccountsAsync(CancellationToken cancellationToken) =>
        dbContext.Database.ExecuteSqlAsync($"SELECT pg_advisory_xact_lock({AccountsLockKey})", cancellationToken);

    /// <summary>True when this account is the only administrator left who is not disabled.</summary>
    private async Task<bool> IsLastActiveAdministratorAsync(User user, CancellationToken cancellationToken)
    {
        if (!user.IsAdministrator || user.IsDisabled)
        {
            return false;
        }

        var others = await dbContext.Users.CountAsync(
            u => u.Id != user.Id && u.Role == UserRole.Administrator && !u.IsDisabled,
            cancellationToken);

        return others == 0;
    }

    /// <summary>
    /// A negative limit used to be stored and then read by Requests as "no limit" — the opposite of a
    /// cap, from a value that could only have been a mistake.
    /// </summary>
    private static Error? InvalidLimit(UserPermissions permissions) =>
        permissions.OpenRequestLimit is < 0 or > MaximumOpenRequestLimit
            ? new Error(
                "identity.invalid_open_request_limit",
                $"The open-request limit must be between 0 (no limit) and {MaximumOpenRequestLimit}, or left to the installation default.")
            : null;

    private static bool IsStoredCeiling(User user, string? ceiling) =>
        user.ContentCeiling is { } stored
        && !string.IsNullOrWhiteSpace(ceiling)
        && string.Equals(stored, ceiling.Trim(), StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// A blank ceiling clears both columns. A named one is stored in the ladder's own spelling, stamped
    /// with the region it was chosen against. An unknown label or a region this installation cannot
    /// order is refused — accepting it would report a restriction that never applies.
    /// </summary>
    private Result<UserPermissions> NormalizeCeiling(UserPermissions permissions)
    {
        if (string.IsNullOrWhiteSpace(permissions.ContentCeiling))
        {
            return Result<UserPermissions>.Success(permissions with { ContentCeiling = null, ContentCeilingRegion = null });
        }

        var current = contentRatingRegion.Current;
        if (string.IsNullOrWhiteSpace(current) || !ContentRatingScale.HasLadder(current))
        {
            return Result<UserPermissions>.Failure(new Error(
                "identity.content_ceiling_unavailable",
                "Name a classification region this installation can order before setting a ceiling."));
        }

        if (!ContentRatingScale.TryCanonical(current, permissions.ContentCeiling, out var canonical))
        {
            return Result<UserPermissions>.Failure(new Error(
                "identity.invalid_content_ceiling",
                "That classification is not on this region's scale."));
        }

        return Result<UserPermissions>.Success(permissions with
        {
            ContentCeiling = canonical,
            ContentCeilingRegion = current.Trim().ToUpperInvariant(),
        });
    }

    /// <summary>Shared credential rules; returns the normalized username on success.</summary>
    private static Result<string> Validate(string username, string password)
    {
        var normalized = Usernames.Normalize(username);
        if (string.IsNullOrWhiteSpace(normalized) || normalized.Length > CredentialLimits.MaximumUsernameLength)
        {
            return Result<string>.Failure(new Error(
                "identity.invalid_username",
                $"Username must not be empty and must be at most {CredentialLimits.MaximumUsernameLength} characters."));
        }

        if (password.Length < CredentialLimits.MinimumPasswordLength)
        {
            return Result<string>.Failure(new Error(
                "identity.weak_password",
                $"Password must be at least {CredentialLimits.MinimumPasswordLength} characters."));
        }

        if (password.Length > CredentialLimits.MaximumPasswordLength)
        {
            return Result<string>.Failure(new Error(
                "identity.password_too_long",
                $"Password must be at most {CredentialLimits.MaximumPasswordLength} characters."));
        }

        return Result<string>.Success(normalized);
    }

    private async Task<Result<UserId>> PersistAsync(
        string normalizedUsername,
        string password,
        UserRole role,
        UserPermissions permissions,
        CancellationToken cancellationToken,
        bool firstRunOnly = false)
    {
        if (!Enum.IsDefined(role))
        {
            return Result<UserId>.Failure(UnknownRole);
        }

        if (InvalidLimit(permissions) is { } invalidLimit)
        {
            return Result<UserId>.Failure(invalidLimit);
        }

        var normalizedPermissions = NormalizeCeiling(permissions);
        if (normalizedPermissions.IsFailure)
        {
            return Result<UserId>.Failure(normalizedPermissions.Error);
        }

        if (role == UserRole.Administrator && normalizedPermissions.Value.ContentCeiling is not null)
        {
            return Result<UserId>.Failure(new Error(
                "identity.administrator_permissions", "An administrator is not restricted by a content ceiling."));
        }

        var user = new User
        {
            Id = Uuid7.New(),
            Username = normalizedUsername,
            PasswordHash = passwordHasher.Hash(password),
            Role = role,
            CanRequest = normalizedPermissions.Value.CanRequest,
            RequestsAutoApproved = normalizedPermissions.Value.RequestsAutoApproved,
            OpenRequestLimit = normalizedPermissions.Value.OpenRequestLimit,
            ContentCeiling = role == UserRole.Administrator ? null : normalizedPermissions.Value.ContentCeiling,
            ContentCeilingRegion = role == UserRole.Administrator ? null : normalizedPermissions.Value.ContentCeilingRegion,
            IsDisabled = false,
            CreatedAt = DateTimeOffset.UtcNow,
        };

        try
        {
            // The account row and its UserCreated event commit together, or not at all.
            var closed = false;
            await unitOfWork.ExecuteAsync(async token =>
            {
                if (firstRunOnly)
                {
                    await LockAccountsAsync(token);
                    if (await dbContext.Users.AnyAsync(token))
                    {
                        closed = true;
                        return;
                    }
                }

                dbContext.Users.Add(user);
                await dbContext.SaveChangesAsync(token);
                await eventBus.PublishAsync(new UserCreated(user.Id, user.Username), token);
            }, cancellationToken);

            if (closed)
            {
                return Result<UserId>.Failure(SetupClosed);
            }
        }
        catch (DbUpdateException ex) when (IsUsernameTaken(ex))
        {
            // Two invites for the same name at once: ux_users_username decided, and the loser gets the
            // same answer the check above would have given a moment later.
            dbContext.Entry(user).State = EntityState.Detached;
            return Result<UserId>.Failure(new Error("identity.username_taken", "That username is already taken."));
        }

        return Result<UserId>.Success(new UserId(user.Id));
    }

    /// <summary>An administrative change to an account emits no event; it is a plain write.</summary>
    private Task SaveAsync(CancellationToken cancellationToken) =>
        unitOfWork.ExecuteAsync(async token => await dbContext.SaveChangesAsync(token), cancellationToken);

    /// <summary>True when the failure is the unique-username violation (a concurrent create).</summary>
    private static bool IsUsernameTaken(DbUpdateException ex) =>
        ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation };
}
