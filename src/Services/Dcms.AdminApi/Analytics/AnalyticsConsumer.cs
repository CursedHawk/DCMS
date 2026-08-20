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
                Device = evt.Device,
                Browser = evt.Browser,
                Os = evt.Os,
                Country = evt.Country,
                UtmSource = evt.UtmSource,
                UtmMedium = evt.UtmMedium,
                UtmCampaign = evt.UtmCampaign,
            });

            // Rollups key on the *path without its query*: "/pricing?utm_source=x" and
            // "/pricing" are the same page, and keying on the raw path fragmented the
            // top-pages table into one row per campaign link.
            var rollupPath = RollupPath(evt.Path);
            var day = DateOnly.FromDateTime(evt.OccurredAt.UtcDateTime);
            var rollup = await db.DailyRollups.FirstOrDefaultAsync(
                r => r.TenantId == batch.TenantId && r.Day == day && r.Type == evt.Type && r.Path == rollupPath, ct);
            if (rollup is null)
            {
                db.DailyRollups.Add(new DailyRollup
                {
                    TenantId = batch.TenantId, Day = day, Type = evt.Type, Path = rollupPath, Count = 1,
                });
            }
            else
            {
                rollup.Count++;
            }
        }

        await db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// The page a URL refers to, for aggregation: query and fragment stripped, a
    /// trailing slash removed (but "/" kept), and clamped to the column width.
    /// </summary>
    private static string RollupPath(string path)
    {
        if (string.IsNullOrEmpty(path)) return "/";
        var cut = path.AsSpan();
        var q = cut.IndexOfAny('?', '#');
        if (q >= 0) cut = cut[..q];
        var value = cut.ToString();
        if (value.Length == 0) return "/";
        if (value.Length > 1 && value.EndsWith('/')) value = value.TrimEnd('/');
        return value.Length > 1024 ? value[..1024] : value;
    }
}
