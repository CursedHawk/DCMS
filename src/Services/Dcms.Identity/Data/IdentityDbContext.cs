using Dcms.Identity.Domain;
using Dcms.Identity.Forgejo;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;

namespace Dcms.Identity.Data;

/// <summary>
/// Owns the "identity" schema: ASP.NET Core Identity tables plus the OpenIddict
/// entity sets (applications, authorizations, scopes, tokens), and the Forgejo
/// user-sync outbox.
/// </summary>
public sealed class IdentityDbContext(DbContextOptions<IdentityDbContext> options)
    : IdentityDbContext<DcmsUser, DcmsRole, Guid>(options)
{
    public const string Schema = "identity";

    public DbSet<ForgejoSyncOutbox> ForgejoSyncOutbox => Set<ForgejoSyncOutbox>();

    /// <summary>Interactive logins that have been ended from another device. See
    /// <see cref="LoginSessions"/>.</summary>
    public DbSet<RevokedLoginSession> RevokedLoginSessions => Set<RevokedLoginSession>();

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);
        builder.HasDefaultSchema(Schema);
        builder.UseOpenIddict();

        builder.Entity<DcmsUser>(e =>
        {
            e.Property(u => u.ForgejoUsername).HasMaxLength(64);
            e.HasIndex(u => u.ForgejoUsername).IsUnique().HasFilter(null);
        });

        builder.Entity<RevokedLoginSession>(e =>
        {
            e.ToTable("revoked_login_sessions");
            // The id is the key: every read is "has this login been ended", by id, and there is
            // no second way to ask.
            e.HasKey(r => r.Id);
            e.Property(r => r.Id).HasMaxLength(128);
            // Drives the prune on write.
            e.HasIndex(r => r.ExpiresAt);
        });

        builder.Entity<ForgejoSyncOutbox>(e =>
        {
            e.ToTable("forgejo_sync_outbox");
            e.HasKey(o => o.Id);
            e.Property(o => o.Username).HasMaxLength(64);
            e.Property(o => o.Email).HasMaxLength(256).IsRequired();
            e.Property(o => o.ContextJson).HasColumnType("jsonb");
            // NextAttemptAt drives the worker's due-row query; index it.
            e.HasIndex(o => o.NextAttemptAt);
        });
    }
}
