using System.Text.Json;
using Dcms.Shared.Contracts.Events;
using Dcms.Shared.Contracts.Messaging;
using Dcms.Shared.Data.Cms;
using Dcms.Shared.Messaging;
using Microsoft.EntityFrameworkCore;

namespace Dcms.AdminApi.Cms;

/// <summary>
/// Relays content_outbox rows to NATS at-least-once, then stamps SentAt. Polls
/// every 2s; resilient to NATS/DB being unavailable. Consumers are idempotent.
/// </summary>
public sealed class OutboxDispatcher(
    IServiceProvider services,
    IEventPublisher events,
    ILogger<OutboxDispatcher> logger) : BackgroundService
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(2);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await DispatchBatchAsync(stoppingToken);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Outbox dispatch failed; retrying.");
            }
            await Task.Delay(PollInterval, stoppingToken);
        }
    }

    private async Task DispatchBatchAsync(CancellationToken ct)
    {
        using var scope = services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CmsDbContext>();

        var pending = await db.Outbox
            .Where(o => o.SentAt == null)
            .OrderBy(o => o.OccurredAt)
            .Take(100)
            .ToListAsync(ct);

        foreach (var message in pending)
        {
            await PublishAsync(message, ct);
            message.SentAt = DateTimeOffset.UtcNow;
            message.Attempts++;
        }

        if (pending.Count > 0)
        {
            await db.SaveChangesAsync(ct);
        }
    }

    private async ValueTask PublishAsync(ContentOutboxMessage message, CancellationToken ct)
    {
        switch (message.Subject)
        {
            case Subjects.ContentPublished:
                await events.PublishAsync(message.Subject,
                    JsonSerializer.Deserialize<ContentPublished>(message.PayloadJson)!, ct);
                break;
            case Subjects.ContentUnpublished:
                await events.PublishAsync(message.Subject,
                    JsonSerializer.Deserialize<ContentUnpublished>(message.PayloadJson)!, ct);
                break;
            default:
                logger.LogWarning("Unknown outbox subject {Subject}; skipping.", message.Subject);
                break;
        }
    }
}
