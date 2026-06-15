using Dcms.Shared.Contracts.Events;
using Dcms.Shared.Contracts.Messaging;
using Dcms.Shared.Data.Analytics;
using Microsoft.EntityFrameworkCore;
using NATS.Client.JetStream;
using NATS.Client.JetStream.Models;
using EventEntity = Dcms.Shared.Data.Analytics.AnalyticsEvent;

namespace Dcms.AdminApi.Analytics;

/// <summary>
/// Persists analytics event batches and maintains per-day rollups. Resilient to
/// NATS being unavailable; idempotency is best-effort (raw events are append-only,
/// rollups are incremented — acceptable for analytics).
/// </summary>
public sealed class AnalyticsConsumer(
    INatsJSContext jetStream,
    IServiceProvider services,
    ILogger<AnalyticsConsumer> logger) : BackgroundService
{
    private const string DurableName = "admin-api-analytics";

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var consumer = await jetStream.CreateOrUpdateConsumerAsync(
                    Streams.Analytics,
                    new ConsumerConfig(DurableName) { AckPolicy = ConsumerConfigAckPolicy.Explicit },
                    stoppingToken);

                await foreach (var msg in consumer.ConsumeAsync<AnalyticsEventBatch>(cancellationToken: stoppingToken))
                {
                    try
                    {
                        if (msg.Data is { } batch)
                        {
                            await PersistAsync(batch, stoppingToken);
                        }
                        await msg.AckAsync(cancellationToken: stoppingToken);
                    }
                    catch (Exception ex)
                    {
                        logger.LogWarning(ex, "Analytics batch failed; will redeliver.");
                        await msg.NakAsync(cancellationToken: stoppingToken);
                    }
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Analytics consumer unavailable; retrying in 5s.");
                await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
            }
        }
    }

    private async Task PersistAsync(AnalyticsEventBatch batch, CancellationToken ct)
    {
        using var scope = services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AnalyticsDbContext>();

        foreach (var evt in batch.Events)
        {
            db.Events.Add(new EventEntity
            {
                TenantId = batch.TenantId,
                OccurredAt = evt.OccurredAt,
                Type = evt.Type,
                Path = evt.Path,
                Referrer = evt.Referrer,
                SessionId = evt.SessionId,
                VisitorHash = evt.VisitorHash,
                PropsJson = evt.Props?.GetRawText() ?? "{}",
            });

            var day = DateOnly.FromDateTime(evt.OccurredAt.UtcDateTime);
            var rollup = await db.DailyRollups.FirstOrDefaultAsync(
                r => r.TenantId == batch.TenantId && r.Day == day && r.Type == evt.Type && r.Path == evt.Path, ct);
            if (rollup is null)
            {
                db.DailyRollups.Add(new DailyRollup
                {
                    TenantId = batch.TenantId, Day = day, Type = evt.Type, Path = evt.Path, Count = 1,
                });
            }
            else
            {
                rollup.Count++;
            }
        }

        await db.SaveChangesAsync(ct);
    }
}
