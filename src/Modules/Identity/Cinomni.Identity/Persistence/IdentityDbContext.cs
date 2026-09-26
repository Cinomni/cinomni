using Cinomni.Identity.Application;
using Microsoft.EntityFrameworkCore;

namespace Cinomni.Identity.Persistence;

/// <summary>
/// EF Core context for the Identity module. Owns the <c>identity</c> schema;
/// no other module reads or writes its tables.
/// </summary>
public sealed class IdentityDbContext(DbContextOptions<IdentityDbContext> options)
    : DbContext(options)
{
    public const string SchemaName = "identity";

    public DbSet<User> Users => Set<User>();

    public DbSet<Session> Sessions => Set<Session>();

    public DbSet<RecoveryCode> RecoveryCodes => Set<RecoveryCode>();

    public DbSet<LoginChallenge> LoginChallenges => Set<LoginChallenge>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema(SchemaName);

        var user = modelBuilder.Entity<User>();
        user.ToTable("users");
        user.HasKey(x => x.Id);
        user.Property(x => x.Username).HasMaxLength(CredentialLimits.MaximumUsernameLength);
        // Enums stored as text — stable and readable, no magic integers.
        user.Property(x => x.Role).HasConversion<string>().HasMaxLength(20);
        user.HasIndex(x => x.Username).IsUnique().HasDatabaseName("ux_users_username");

        user.Property(x => x.TotpSecretKeyId).HasMaxLength(32);
        user.Property(x => x.ContentCeiling).HasMaxLength(20);
        user.Property(x => x.ContentCeilingRegion).HasMaxLength(2);
        // A cipher without the nonce or the key id that produced it can never be read back, and a
        // ceiling without the region it was chosen in cannot be compared. The database refuses both
        // rather than storing a row only the entity keeps coherent.
        user.ToTable("users", t =>
        {
            t.HasCheckConstraint(
                "ck_users_totp_secret_complete",
                "(totp_secret_cipher IS NULL) = (totp_secret_nonce IS NULL) "
                + "AND (totp_secret_cipher IS NULL) = (totp_secret_key_id IS NULL)");
            t.HasCheckConstraint(
                "ck_users_content_ceiling_complete",
                "(content_ceiling IS NULL) = (content_ceiling_region IS NULL)");
        });

        var recovery = modelBuilder.Entity<RecoveryCode>();
        recovery.ToTable("recovery_codes");
        recovery.HasKey(x => x.Id);
        recovery.Property(x => x.CodeHash).HasMaxLength(100);
        // Unique across the installation, not merely per account: two accounts minting the same code
        // is astronomically unlikely and would be a cross-account bypass if it ever happened.
        recovery.HasIndex(x => x.CodeHash).IsUnique().HasDatabaseName("ux_recovery_codes_hash");
        recovery.HasIndex(x => x.UserId).HasDatabaseName("ix_recovery_codes_user");
        recovery.HasOne<User>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);

        var challenge = modelBuilder.Entity<LoginChallenge>();
        challenge.ToTable("login_challenges");
        challenge.HasKey(x => x.Id);
        challenge.Property(x => x.ChallengeHash).HasMaxLength(100);
        challenge.HasIndex(x => x.ChallengeHash).IsUnique().HasDatabaseName("ux_login_challenges_hash");
        // Drives the sweep: a challenge lives for minutes, so they accumulate faster than sessions do.
        challenge.HasIndex(x => x.ExpiresAt).HasDatabaseName("ix_login_challenges_expires_at");
        challenge.HasOne<User>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);

        var session = modelBuilder.Entity<Session>();
        session.ToTable("sessions");
        session.HasKey(x => x.Id);
        session.Property(x => x.TokenHash).HasMaxLength(100);
        session.HasIndex(x => x.TokenHash).IsUnique().HasDatabaseName("ux_sessions_token_hash");
        session.HasIndex(x => x.UserId).HasDatabaseName("ix_sessions_user");
        // Drives the retention sweep over sessions that can no longer authenticate.
        session.HasIndex(x => x.ExpiresAt).HasDatabaseName("ix_sessions_expires_at");
        // Intra-module foreign key (same schema): allowed, unlike cross-module references.
        session.HasOne<User>()
            .WithMany()
            .HasForeignKey(x => x.UserId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
