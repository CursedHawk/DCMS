using Dcms.Shared.Data.Audit;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Dcms.Shared.Data.Ai;

// rls: defines the context; with no query filters, every reader decides (RlsScopeCoverageTests).
/// <summary>
/// Owns the "ai" schema. Two things live here: provider configuration
/// (tenant_ai_settings, user_ai_settings), written by admin-api's settings UI and
/// read by ai-gateway for provider resolution; and the assistant's stored
/// conversations (conversations, messages), written and read by admin-api alone.
///
/// Settings rows are keyed by tenant (or tenant+user), so isolation is by primary
/// key rather than a query filter. The conversation tables are not — they hold many
/// rows per tenant — so they carry a TenantId column and are covered by the RLS
/// backstop; see <see cref="Dcms.Shared.Data.Rls.RlsConfigurator"/>.
/// </summary>
public class AiDbContext(DbContextOptions<AiDbContext> options) : ScopedDbContext(options)
{
    public const string Schema = "ai";

    public DbSet<TenantAiSettings> Settings => Set<TenantAiSettings>();
    public DbSet<AiProviderConfig> Providers => Set<AiProviderConfig>();
    public DbSet<AiUsageDay> Usage => Set<AiUsageDay>();
    public DbSet<AiKnowledgeEntry> Knowledge => Set<AiKnowledgeEntry>();
    public DbSet<UserAiSettings> UserSettings => Set<UserAiSettings>();
    public DbSet<AiConversation> Conversations => Set<AiConversation>();
    public DbSet<AiMessage> Messages => Set<AiMessage>();
    public DbSet<AiRun> Runs => Set<AiRun>();

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);

        // Audit records are written by the same SaveChanges as the change they describe.
        builder.MapAuditOutbox();
        builder.HasDefaultSchema(Schema);

        builder.Entity<TenantAiSettings>(e =>
        {
            e.ToTable("tenant_ai_settings");
            e.HasKey(s => s.TenantId);
            e.Property(s => s.PromptOverridesJson).HasColumnType("jsonb");
        });

        builder.Entity<AiProviderConfig>(e =>
        {
            e.ToTable("providers");
            e.HasKey(p => p.Id);
            e.Property(p => p.Name).HasMaxLength(AiProviderConfig.MaxName).IsRequired();
            e.Property(p => p.Provider).HasConversion<string>().HasMaxLength(16);
            e.Property(p => p.Model).HasMaxLength(128);
            e.Property(p => p.BaseUrl).HasMaxLength(512);
            e.HasIndex(p => new { p.TenantId, p.Name }).IsUnique();
            // One default per workspace.
            e.HasIndex(p => p.TenantId).IsUnique().HasFilter("\"IsDefault\"").HasDatabaseName("IX_providers_TenantId_default");
        });

        builder.Entity<AiUsageDay>(e =>
        {
            e.ToTable("usage");
            e.HasKey(u => u.Id);
            e.Property(u => u.Source).HasMaxLength(16).IsRequired();
            e.Property(u => u.ProviderName).HasMaxLength(AiProviderConfig.MaxName).IsRequired();
            e.Property(u => u.ProviderKind).HasMaxLength(16).IsRequired();
            e.Property(u => u.Model).HasMaxLength(128).IsRequired();
            // The gateway's ON CONFLICT target: one row per day, payer, provider and model.
            e.HasIndex(u => new { u.TenantId, u.Day, u.Source, u.ProviderId, u.UserId, u.Model })
                .IsUnique().AreNullsDistinct(false).HasDatabaseName("IX_usage_key");
        });

        builder.Entity<AiKnowledgeEntry>(e =>
        {
            e.ToTable("knowledge");
            e.HasKey(k => k.Id);
            e.Property(k => k.Path).HasMaxLength(200).IsRequired();
            e.Property(k => k.Title).HasMaxLength(200).IsRequired();
            e.Property(k => k.Description).HasMaxLength(500).IsRequired();
            e.Property(k => k.Source).HasMaxLength(16).IsRequired();
            // One entry per path in a scope; the workspace scope (null site) is a scope too.
            e.HasIndex(k => new { k.TenantId, k.SiteId, k.Path }).IsUnique().AreNullsDistinct(false);
            e.HasGeneratedTsVectorColumn(k => k.SearchVector, "simple", k => new { k.Title, k.Description, k.Body })
                .HasIndex(k => k.SearchVector)
                .HasMethod("GIN");
        });

        builder.Entity<UserAiSettings>(e =>
        {
            e.ToTable("user_ai_settings");
            e.HasKey(s => new { s.TenantId, s.UserId });
            e.Property(s => s.Provider).HasConversion<string>().HasMaxLength(16);
            e.Property(s => s.Model).HasMaxLength(128);
            e.Property(s => s.BaseUrl).HasMaxLength(512);
        });

        builder.Entity<AiConversation>(e =>
        {
            e.ToTable("conversations");
            e.HasKey(c => c.Id);
            e.Property(c => c.Title).HasMaxLength(160).IsRequired();
            e.Property(c => c.Visibility).HasConversion<string>().HasMaxLength(16);
            e.Property(c => c.Mode).HasMaxLength(16);
            e.Property(c => c.PageArea).HasMaxLength(64);
            /*
             * Defaulted in the database, not just in C#.
             *
             * Every row that existed before this column did is a console conversation, and a
             * NOT NULL column added without a default backfills them with the empty string —
             * which is not a surface any rail filters on, so the entire existing history would
             * have quietly disappeared from the assistant's list. The default is what backfills
             * them correctly, and it stays afterwards as the right answer for any writer that
             * does not name a surface.
             */
            e.Property(c => c.Surface)
                .HasMaxLength(16)
                .HasDefaultValue(AiSurfaces.Console)
                .IsRequired();
            e.Property(c => c.Branch).HasMaxLength(200);

            // The rail's query in one index: this tenant, newest first, archived rows excluded.
            e.HasIndex(c => new { c.TenantId, c.OwnerUserId, c.UpdatedAt });
            e.HasIndex(c => new { c.TenantId, c.Visibility, c.UpdatedAt });

            // The IDE rail: one member's conversations about one site, newest first. Without
            // this the surface column would filter after the scan and the IDE's many short
            // runs would be paying for the console's history on every open.
            e.HasIndex(c => new { c.TenantId, c.OwnerUserId, c.Surface, c.SiteId, c.UpdatedAt });
        });

        builder.Entity<AiMessage>(e =>
        {
            e.ToTable("messages");
            e.HasKey(m => m.Id);
            e.Property(m => m.Role).HasMaxLength(16).IsRequired();
            e.Property(m => m.Content).HasColumnType("jsonb").IsRequired();
            e.Property(m => m.Kind).HasMaxLength(16);

            // Appending a turn races nothing but itself, and a retried append must not be able
            // to produce two turns at the same position.
            e.HasIndex(m => new { m.ConversationId, m.Seq }).IsUnique();

            e.HasOne(m => m.Conversation)
                .WithMany(c => c.Messages)
                .HasForeignKey(m => m.ConversationId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        builder.Entity<AiRun>(e =>
        {
            e.ToTable("runs");
            e.HasKey(r => r.Id);
            e.Property(r => r.Task).HasMaxLength(2000).IsRequired();
            e.Property(r => r.Outcome).HasMaxLength(16).IsRequired();
            e.Property(r => r.Complexity).HasMaxLength(16);
            e.Property(r => r.ChangesJson).HasColumnType("jsonb").IsRequired();
            e.Property(r => r.ValidationJson).HasColumnType("jsonb");
            e.Property(r => r.MetricsJson).HasColumnType("jsonb");
            e.Property(r => r.ClientId).HasMaxLength(100);

            e.HasIndex(r => new { r.ConversationId, r.StartedAt });

            e.HasOne(r => r.Conversation)
                .WithMany(c => c.Runs)
                .HasForeignKey(r => r.ConversationId)
                .OnDelete(DeleteBehavior.Cascade);
        });
    }
}

public sealed class AiDbContextFactory : IDesignTimeDbContextFactory<AiDbContext>
{
    public AiDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<AiDbContext>()
            .UseNpgsql(
                "Host=localhost;Port=5432;Database=dcms;Username=dcms;Password=dcms-dev",
                npgsql => npgsql.MigrationsHistoryTable("__ef_migrations_history", AiDbContext.Schema))
            .Options;
        return new AiDbContext(options);
    }
}

public static class AiServiceCollectionExtensions
{
    public static IServiceCollection AddDcmsAiData(this IServiceCollection services, IConfiguration configuration)
    {
        var connectionString = configuration.GetConnectionString("Postgres")
                               ?? "Host=localhost;Port=5432;Database=dcms;Username=dcms;Password=dcms-dev";

        services.AddDcmsDbContext<AiDbContext>(connectionString, AiDbContext.Schema);

        return services;
    }
}
