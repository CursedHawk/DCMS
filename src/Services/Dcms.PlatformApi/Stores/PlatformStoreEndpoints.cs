using Dcms.PlatformApi.Observability;
using Dcms.Shared.Security;
using Microsoft.Extensions.Options;

namespace Dcms.PlatformApi.Stores;

/// <summary>
/// What each telemetry store is holding, against the budget it was sized to.
///
/// <para>Reads the recording rules that already exist rather than computing anything:
/// <c>dcms_store_disk_bytes</c> and <c>dcms_store_disk_budget_bytes</c> come from the
/// <c>store-usage</c> sidecar, and the peak, growth and projection are
/// <c>dcms:store_disk_*</c> rules the retention dashboard is already built on. Duplicating that
/// arithmetic here would give the console and the dashboard two ways to disagree about whether
/// a store is over budget.</para>
/// </summary>
public static class PlatformStoreEndpoints
{
    public sealed record StoreRow(
        string Store,
        double UsedBytes,
        double BudgetBytes,
        double PeakBytes,
        double ProjectedBytes,
        double GrowthBytesPerSecond,
        double RetentionSeconds,
        bool CanPurge,
        string? PurgeNote);

    /// <summary>
    /// What each store can actually be told to delete, which is not the same for any two of
    /// them and is the first thing an operator needs to know.
    /// </summary>
    private static (bool CanPurge, string? Note) Capability(string store) => store switch
    {
        "loki" => (true,
            "Deletes by stream selector and time range. Takes effect after Loki's 2-hour delay, "
            + "so a mistake can be cancelled until then. Audit and security streams are refused."),
        "prometheus" => (true,
            "Deletes series by matcher. Disk is only reclaimed once tombstones are cleaned, "
            + "which this does straight after."),
        "tempo" => (false,
            "Tempo has no delete API. Traces leave only by ageing out of the 7-day block retention."),
        "grafana" => (false,
            "Dashboards and alert state, provisioned from files. Nothing here is worth deleting."),
        "alloy" => (false,
            "The collector's write-ahead log. It manages its own size and is not safe to truncate."),
        _ => (false, null),
    };

    public static IEndpointRouteBuilder MapPlatformStoreEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/platform/stores", async (
            PrometheusClient prometheus, IOptions<ObservabilityOptions> options, CancellationToken ct) =>
        {
            // One query per rule rather than one per store: Prometheus returns every store's
            // series in a single response, and five round trips to draw one page is how a
            // console becomes slower than the thing it is watching.
            var (reachable, used) = await prometheus.QueryAsync("dcms_store_disk_bytes", ct);
            var (_, budget) = await prometheus.QueryAsync("dcms_store_disk_budget_bytes", ct);
            var (_, peak) = await prometheus.QueryAsync("dcms:store_disk_peak_bytes", ct);
            var (_, projected) = await prometheus.QueryAsync("dcms:store_disk_projected_bytes", ct);
            var (_, growth) = await prometheus.QueryAsync("dcms:store_disk_growth_bytes_per_second", ct);
            var (_, retention) = await prometheus.QueryAsync("dcms:store_retention_seconds", ct);

            static Dictionary<string, double> ByStore(
                IReadOnlyList<(IReadOnlyDictionary<string, string> Labels, double Value)> rows) =>
                rows.Where(r => r.Labels.ContainsKey("store"))
                    .GroupBy(r => r.Labels["store"], StringComparer.Ordinal)
                    .ToDictionary(g => g.Key, g => g.First().Value, StringComparer.Ordinal);

            var usedByStore = ByStore(used);
            var budgetByStore = ByStore(budget);
            var peakByStore = ByStore(peak);
            var projectedByStore = ByStore(projected);
            var growthByStore = ByStore(growth);
            var retentionByStore = ByStore(retention);

            var rows = usedByStore.Keys
                .Union(budgetByStore.Keys, StringComparer.Ordinal)
                .OrderBy(s => s, StringComparer.Ordinal)
                .Select(store =>
                {
                    var (canPurge, note) = Capability(store);
                    // The Docker-log janitor is a deployment choice, so a store whose purge
                    // depends on it reports itself as un-purgeable when it is switched off
                    // rather than offering a button that 503s.
                    return new StoreRow(
                        store,
                        usedByStore.GetValueOrDefault(store),
                        budgetByStore.GetValueOrDefault(store),
                        peakByStore.GetValueOrDefault(store),
                        projectedByStore.GetValueOrDefault(store),
                        growthByStore.GetValueOrDefault(store),
                        retentionByStore.GetValueOrDefault(store),
                        canPurge,
                        note);
                })
                .ToList();

            return Results.Ok(new
            {
                stores = rows,
                // Told to the console rather than inferred by it: whether container logs can be
                // truncated is a property of how this platform was deployed.
                dockerLogPurgeAvailable = options.Value.LogJanitorEnabled,
                // Reachability, not emptiness. Prometheus answering with no store series means
                // the store-usage sidecar has not reported yet, which is a different problem
                // from Prometheus being down and sends an operator somewhere different.
                prometheusUnreachable = !reachable,
                awaitingStoreMetrics = reachable && rows.Count == 0,
            });
        })
        .RequirePlatformPermission(PlatformConsolePermissions.LogsRead)
        .WithName("PlatformStores");

        return app;
    }
}
