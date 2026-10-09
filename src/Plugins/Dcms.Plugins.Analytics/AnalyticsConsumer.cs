using Dcms.Shared.Contracts.Events;
using Dcms.Shared.Contracts.Messaging;
using Dcms.Shared.Data.Analytics;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NATS.Client.JetStream;
using NATS.Client.JetStream.Models;
using Dcms.Shared.Data.Rls;

namespace Dcms.Plugins.Analytics;

/// <summary>
/// Persists analytics event batches and maintains per-day rollups. Resilient to
/// NATS being unavailable, and idempotent: each hit is stored under its ingest id, and a
/// redelivered batch (committed, then the ack lost) conflicts on that id and increments
/// nothing — before, it stored the hit twice and counted it twice.
/// </summary>
internal sealed class AnalyticsConsumer(
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
        // One batch is one tenant's visitors; its rows and rollups are written as that tenant (ADR 0015).
        using var rls = RlsScope.Tenant(batch.TenantId);
        using var scope = services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AnalyticsDbContext>();

        // The row and its rollup increment commit together, so "the row exists" is exactly
        // "it was counted" — which is what lets a conflict on the row skip the increment.
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        for (var i = 0; i < batch.Events.Count; i++)
        {
            var evt = batch.Events[i];
            var eventId = EventKey(batch.EventId, i);
            var props = evt.Props?.GetRawText() ?? "{}";

            var inserted = await db.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO analytics.events ("TenantId", "OccurredAt", "Type", "Path", "Referrer", "SessionId",
                    "VisitorHash", "PropsJson", "Device", "Browser", "Os", "Country", "UtmSource", "UtmMedium",
                    "UtmCampaign", "SiteId", "Hostname", "EventId")
                VALUES ({batch.TenantId}, {evt.OccurredAt}, {evt.Type}, {evt.Path}, {evt.Referrer}, {evt.SessionId},
                    {evt.VisitorHash}, CAST({props} AS jsonb), {evt.Device}, {evt.Browser}, {evt.Os}, {evt.Country},
                    {evt.UtmSource}, {evt.UtmMedium}, {evt.UtmCampaign}, {evt.SiteId}, {evt.Hostname}, {eventId})
                ON CONFLICT ("TenantId", "EventId") WHERE "EventId" IS NOT NULL DO NOTHING
                """, ct);
            if (inserted == 0)
            {
                continue;
            }

            // Rollups key on the *path without its query*: "/pricing?utm_source=x" and
            // "/pricing" are the same page, and keying on the raw path fragmented the
            // top-pages table into one row per campaign link. An upsert rather than
            // read-then-write, so two writers cannot both read the old count.
            var rollupPath = RollupPath(evt.Path);
            var day = DateOnly.FromDateTime(evt.OccurredAt.UtcDateTime);
            var site = evt.SiteId ?? Guid.Empty;
            await db.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO analytics.daily_rollups ("TenantId", "Day", "SiteId", "Type", "Path", "Count")
                VALUES ({batch.TenantId}, {day}, {site}, {evt.Type}, {rollupPath}, 1)
                ON CONFLICT ("TenantId", "Day", "SiteId", "Type", "Path")
                DO UPDATE SET "Count" = analytics.daily_rollups."Count" + 1
                """, ct);
        }
        await tx.CommitAsync(ct);
    }

    /// <summary>
    /// A stable id per event in a batch: the batch id itself for the first (every producer sends
    /// one event per batch today), and a deterministic variant of it for any after that.
    /// </summary>
    internal static Guid EventKey(Guid batchId, int index)
    {
        if (index == 0)
        {
            return batchId;
        }
        Span<byte> bytes = stackalloc byte[16];
        batchId.TryWriteBytes(bytes);
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(bytes[12..],
            System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(bytes[12..]) ^ index);
        return new Guid(bytes);
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
