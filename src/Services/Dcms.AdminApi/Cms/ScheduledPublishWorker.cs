using System.Text.Json;
using Dcms.Shared.Contracts.Events;
using Dcms.Shared.Contracts.Messaging;
using Dcms.Shared.Data.Cms;
using Microsoft.EntityFrameworkCore;

namespace Dcms.AdminApi.Cms;

/// <summary>
/// Publishes due scheduled content. Polls every 15s and claims rows with
/// FOR UPDATE SKIP LOCKED inside a transaction, so multiple admin-api replicas
/// can run this concurrently and each due item publishes exactly once. The
/// content.published event is written to the same outbox as manual publishes.
/// </summary>
public sealed class ScheduledPublishWorker(
    IServiceProvider services, IConfiguration configuration, ILogger<ScheduledPublishWorker> logger)
    : BackgroundService
{
    private readonly TimeSpan _pollInterval = TimeSpan.FromSeconds(configuration.GetValue("Scheduler:PollSeconds", 15));

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await ProcessDueAsync(stoppingToken);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Scheduled-publish poll failed; retrying.");
            }
            await Task.Delay(_pollInterval, stoppingToken);
        }
    }

    private async Task ProcessDueAsync(CancellationToken ct)
    {
        using var scope = services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CmsDbContext>();

        await using var tx = await db.Database.BeginTransactionAsync(ct);

        // Claim due rows; SKIP LOCKED lets sibling replicas grab disjoint sets.
        var due = await db.ScheduledPublishes.FromSqlRaw(
            """
            SELECT * FROM cms.scheduled_publishes
            WHERE status = 'Pending' AND publish_at <= now()
            ORDER BY publish_at
            FOR UPDATE SKIP LOCKED
            LIMIT 50
            """).ToListAsync(ct);

        if (due.Count == 0)
        {
            await tx.RollbackAsync(ct);
            return;
        }

        foreach (var schedule in due)
        {
            var item = await db.ContentItems.IgnoreQueryFilters()
                .FirstOrDefaultAsync(c => c.Id == schedule.ItemId, ct);
            if (item is null)
            {
                schedule.Status = ScheduledPublishStatus.Failed;
                schedule.Error = "Content item not found.";
                schedule.ProcessedAt = DateTimeOffset.UtcNow;
                continue;
            }

            item.PublishedVersionId = schedule.VersionId;
            item.Status = ContentStatus.Published;
            item.PublishedAt = DateTimeOffset.UtcNow;
            item.UpdatedAt = item.PublishedAt.Value;

            var evt = new ContentPublished(Guid.NewGuid(), DateTimeOffset.UtcNow,
                schedule.TenantId, item.PluginInstanceId, item.Id, item.ContentType, item.Slug);
            db.Outbox.Add(new ContentOutboxMessage
            {
                Id = Guid.NewGuid(),
                TenantId = schedule.TenantId,
                Subject = Subjects.ContentPublished,
                PayloadJson = JsonSerializer.Serialize(evt),
            });

            schedule.Status = ScheduledPublishStatus.Done;
            schedule.ProcessedAt = DateTimeOffset.UtcNow;
        }

        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        logger.LogInformation("Published {Count} scheduled item(s).", due.Count);
    }
}
