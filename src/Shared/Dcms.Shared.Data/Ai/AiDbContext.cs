using Dcms.Shared.Data.Audit;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Dcms.Shared.Data.Ai;

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
public class AiDbContext(DbContextOptions<AiDbContext> options) : DbContext(options)
{
    public const string Schema = "ai";

    public DbSet<TenantAiSettings> Settings => Set<TenantAiSettings>();
    public DbSet<UserAiSettings> UserSettings => Set<UserAiSettings>();
    public DbSet<AiConversation> Conversations => Set<AiConversation>();
    public DbSet<AiMessage> Messages => Set<AiMessage>();

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
            e.Property(s => s.Provider).HasConversion<string>().HasMaxLength(16);
            e.Property(s => s.Model).HasMaxLength(128);
            e.Property(s => s.BaseUrl).HasMaxLength(512);
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

            // The rail's query in one index: this tenant, newest first, archived rows excluded.
            e.HasIndex(c => new { c.TenantId, c.OwnerUserId, c.UpdatedAt });
            e.HasIndex(c => new { c.TenantId, c.Visibility, c.UpdatedAt });
        });

        builder.Entity<AiMessage>(e =>
        {
            e.ToTable("messages");
            e.HasKey(m => m.Id);
            e.Property(m => m.Role).HasMaxLength(16).IsRequired();
            e.Property(m => m.Content).HasColumnType("jsonb").IsRequired();

            // Appending a turn races nothing but itself, and a retried append must not be able
            // to produce two turns at the same position.
            e.HasIndex(m => new { m.ConversationId, m.Seq }).IsUnique();

            e.HasOne(m => m.Conversation)
                .WithMany(c => c.Messages)
                .HasForeignKey(m => m.ConversationId)
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

        services.AddDbContext<AiDbContext>((sp, options) =>
            options.UseNpgsql(connectionString, npgsql =>
                    npgsql.MigrationsHistoryTable("__ef_migrations_history", AiDbContext.Schema))
                .UseDcmsAuditInterceptors(sp));

        return services;
    }
}
