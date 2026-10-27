using Dcms.Shared.Kernel.Abstractions;
using Dcms.Shared.Data.Audit;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Dcms.Shared.Data.Chat;

/// <summary>Who authored a chat message.</summary>
public enum ChatSender
{
    Visitor = 0,
    Agent = 1,

    /// <summary>The AI assistant. Stored as an int like the others — no schema change.</summary>
    Bot = 2,
}

public enum ChatConversationStatus
{
    Open = 0,
    Closed = 1,
}

/// <summary>
/// A live-chat conversation between a website visitor and a tenant agent.
/// The visitor may be authenticated (VisitorId set) or anonymous (identified
/// only by the client-supplied display name + the SignalR connection).
/// </summary>
public sealed class ChatConversation : EnvironmentEntity
{
    public Guid? VisitorId { get; set; }
    public string VisitorName { get; set; } = "Visitor";
    public ChatConversationStatus Status { get; set; } = ChatConversationStatus.Open;
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset LastMessageAt { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class ChatMessage : EnvironmentEntity
{
    public Guid ConversationId { get; set; }
    public ChatSender Sender { get; set; }

    /// <summary>Visitor id or agent (platform) user id, depending on <see cref="Sender"/>. Null for anonymous visitors.</summary>
    public Guid? SenderId { get; set; }
    public string Body { get; set; } = string.Empty;
    public DateTimeOffset SentAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? ReadAt { get; set; }
}

/// <summary>Owns the "chat" schema. Read/written by content-api (visitor hub) and admin-api (agent console).</summary>
public class ChatDbContext(DbContextOptions<ChatDbContext> options) : ScopedDbContext(options)
{
    public const string Schema = "chat";

    private Guid CurrentTenantId => TenantContext?.TenantId ?? Guid.Empty;

    public DbSet<ChatConversation> Conversations => Set<ChatConversation>();
    public DbSet<ChatMessage> Messages => Set<ChatMessage>();

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);

        // Audit records are written by the same SaveChanges as the change they describe.
        builder.MapAuditOutbox();
        builder.HasDefaultSchema(Schema);

        builder.Entity<ChatConversation>(e =>
        {
            e.ToTable("conversations");
            e.HasKey(c => c.Id);
            e.Property(c => c.VisitorName).HasMaxLength(256).IsRequired();
            e.Property(c => c.Status).HasConversion<int>();
            e.HasIndex(c => new { c.TenantId, c.Status, c.LastMessageAt });
            e.HasQueryFilter(QueryFilters.Tenant, c => c.TenantId == CurrentTenantId)
                .HasQueryFilter(QueryFilters.Environment, c => CurrentEnvironmentId == null || c.EnvironmentId == CurrentEnvironmentId);
        });

        builder.Entity<ChatMessage>(e =>
        {
            e.ToTable("messages");
            e.HasKey(m => m.Id);
            e.Property(m => m.Sender).HasConversion<int>();
            e.Property(m => m.Body).HasMaxLength(8000).IsRequired();
            e.HasIndex(m => new { m.TenantId, m.ConversationId, m.SentAt });
            e.HasQueryFilter(QueryFilters.Tenant, m => m.TenantId == CurrentTenantId)
                .HasQueryFilter(QueryFilters.Environment, m => CurrentEnvironmentId == null || m.EnvironmentId == CurrentEnvironmentId);
        });
    }

    public override Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
    {
        Stamp();
        return base.SaveChangesAsync(cancellationToken);
    }

    public override int SaveChanges()
    {
        Stamp();
        return base.SaveChanges();
    }

    private void Stamp()
    {
        var tenantId = CurrentTenantId;
        if (tenantId == Guid.Empty)
        {
            return;
        }
        foreach (var entry in ChangeTracker.Entries<TenantEntity>())
        {
            if (entry.State != EntityState.Added)
            {
                continue;
            }
            if (entry.Entity.TenantId == Guid.Empty)
            {
                entry.Entity.TenantId = tenantId;
            }
        }
    }
}

public sealed class ChatDbContextFactory : IDesignTimeDbContextFactory<ChatDbContext>
{
    public ChatDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<ChatDbContext>()
            .UseNpgsql(
                "Host=localhost;Port=5432;Database=dcms;Username=dcms;Password=dcms-dev",
                npgsql => npgsql.MigrationsHistoryTable("__ef_migrations_history", ChatDbContext.Schema))
            .Options;
        return new ChatDbContext(options).WithTenant(new NullTenantContext());
    }

    private sealed class NullTenantContext : ITenantContext
    {
        public Guid? TenantId => null;
        public string? TenantSlug => null;
    }
}

public static class ChatServiceCollectionExtensions
{
    public static IServiceCollection AddDcmsChatData(this IServiceCollection services, IConfiguration configuration)
    {
        var connectionString = configuration.GetConnectionString("Postgres")
                               ?? "Host=localhost;Port=5432;Database=dcms;Username=dcms;Password=dcms-dev";

        services.AddDcmsDbContext<ChatDbContext>(connectionString, ChatDbContext.Schema);

        return services;
    }
}
