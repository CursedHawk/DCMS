using Dcms.Identity.Domain;
using Dcms.Identity.Forgejo;
using Dcms.Identity.Realms;
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

    /// <summary>Platform accounts signed in per browser. See <see cref="BrowserAccounts"/>.</summary>
    public DbSet<BrowserAccount> BrowserAccounts => Set<BrowserAccount>();

    // Tenant realms (ADR 0022): each tenant's enterprise users, apart from DcmsUser by table.
    // Not under RLS — identity reads no tenant table and holds no tenant context — so every
    // query names its tenant itself; RealmStore is the one place that does.
    public DbSet<Realm> Realms => Set<Realm>();
    public DbSet<RealmUser> RealmUsers => Set<RealmUser>();
    public DbSet<RealmGroup> RealmGroups => Set<RealmGroup>();
    public DbSet<RealmGroupMember> RealmGroupMembers => Set<RealmGroupMember>();
    public DbSet<RealmLogin> RealmLogins => Set<RealmLogin>();
    public DbSet<RealmProvider> RealmProviders => Set<RealmProvider>();

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

        builder.Entity<BrowserAccount>(e =>
        {
            e.ToTable("browser_accounts");
            e.HasKey(r => r.Id);
            e.Property(r => r.DeviceHash).HasMaxLength(64);
            e.Property(r => r.LoginSessionId).HasMaxLength(128);
            e.Property(r => r.SecurityStamp).HasMaxLength(256);
            // One row per account per browser: signing in again resumes it.
            e.HasIndex(r => new { r.DeviceHash, r.UserId }).IsUnique();
            // Every touch and every "has this login ended" asks by login id.
            e.HasIndex(r => r.LoginSessionId);
            // A deleted account leaves nothing to offer.
            e.HasOne<DcmsUser>().WithMany().HasForeignKey(r => r.UserId).OnDelete(DeleteBehavior.Cascade);
        });

        builder.Entity<Realm>(e =>
        {
            e.ToTable("realms");
            e.HasKey(r => r.TenantId);
            e.Property(r => r.Slug).HasMaxLength(63);
            e.Property(r => r.Name).HasMaxLength(200);
            e.HasIndex(r => r.Slug).IsUnique();
        });

        builder.Entity<RealmUser>(e =>
        {
            e.ToTable("realm_users");
            e.HasKey(u => u.Id);
            e.Property(u => u.Email).HasMaxLength(256);
            e.Property(u => u.NormalizedEmail).HasMaxLength(256);
            e.Property(u => u.DisplayName).HasMaxLength(200);
            e.Property(u => u.Status).HasConversion<string>().HasMaxLength(16);
            e.Property(u => u.SecurityStamp).HasMaxLength(64);
            // Unique within a tenant only: the same person may hold an account in every realm.
            e.HasIndex(u => new { u.TenantId, u.NormalizedEmail }).IsUnique();
            e.HasOne<Realm>().WithMany().HasForeignKey(u => u.TenantId).OnDelete(DeleteBehavior.Cascade);
        });

        builder.Entity<RealmGroup>(e =>
        {
            e.ToTable("realm_groups");
            e.HasKey(g => g.Id);
            e.Property(g => g.Name).HasMaxLength(120);
            e.Property(g => g.Description).HasMaxLength(1000);
            e.HasIndex(g => new { g.TenantId, g.Name }).IsUnique();
            e.HasOne<Realm>().WithMany().HasForeignKey(g => g.TenantId).OnDelete(DeleteBehavior.Cascade);
        });

        builder.Entity<RealmGroupMember>(e =>
        {
            e.ToTable("realm_group_members");
            e.HasKey(m => new { m.GroupId, m.UserId });
            e.HasIndex(m => new { m.TenantId, m.UserId });
            e.HasOne<RealmGroup>().WithMany().HasForeignKey(m => m.GroupId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne<RealmUser>().WithMany().HasForeignKey(m => m.UserId).OnDelete(DeleteBehavior.Cascade);
        });

        builder.Entity<RealmLogin>(e =>
        {
            e.ToTable("realm_logins");
            e.HasKey(l => l.Id);
            e.Property(l => l.Provider).HasMaxLength(64);
            e.Property(l => l.ProviderKey).HasMaxLength(256);
            // One account per provider subject per tenant; the same subject may sign in to many tenants.
            e.HasIndex(l => new { l.TenantId, l.Provider, l.ProviderKey }).IsUnique();
            e.HasOne<RealmUser>().WithMany().HasForeignKey(l => l.UserId).OnDelete(DeleteBehavior.Cascade);
        });

        builder.Entity<RealmProvider>(e =>
        {
            e.ToTable("realm_providers");
            e.HasKey(p => p.Id);
            e.Property(p => p.Key).HasMaxLength(40);
            e.Property(p => p.Kind).HasConversion<string>().HasMaxLength(16);
            e.Property(p => p.Provisioning).HasConversion<string>().HasMaxLength(24);
            e.Property(p => p.DisplayName).HasMaxLength(80);
            e.Property(p => p.ClientId).HasMaxLength(256);
            e.Property(p => p.Issuer).HasMaxLength(512);
            e.Property(p => p.EntraTenant).HasMaxLength(256);
            e.Property(p => p.HostedDomain).HasMaxLength(256);
            e.Property(p => p.SecretCiphertext).HasMaxLength(4096);
            e.Property(p => p.GroupClaim).HasMaxLength(128);
            // Through a converter: Npgsql maps a POCO to jsonb only with dynamic JSON switched on.
            e.Property(p => p.GroupMappings).HasColumnType("jsonb").HasConversion(
                v => System.Text.Json.JsonSerializer.Serialize(v, (System.Text.Json.JsonSerializerOptions?)null),
                v => System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, Guid>>(v, (System.Text.Json.JsonSerializerOptions?)null) ?? new(),
                new Microsoft.EntityFrameworkCore.ChangeTracking.ValueComparer<Dictionary<string, Guid>>(
                    (a, b) => a!.Count == b!.Count && !a.Except(b).Any(),
                    v => v.Aggregate(0, (h, kv) => HashCode.Combine(h, kv.Key, kv.Value)),
                    v => new Dictionary<string, Guid>(v)));
            e.HasIndex(p => new { p.TenantId, p.Key }).IsUnique();
            e.HasOne<Realm>().WithMany().HasForeignKey(p => p.TenantId).OnDelete(DeleteBehavior.Cascade);
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
