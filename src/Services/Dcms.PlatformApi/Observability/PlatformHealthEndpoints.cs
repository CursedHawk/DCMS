using Dcms.Shared.Security;
using Dcms.Shared.Security.Authorization;

namespace Dcms.PlatformApi.Observability;

/// <summary>
/// The golden signals, read straight from Prometheus, so the console says something when
/// Grafana does not.
///
/// <para><b>Why this exists next to 24 provisioned dashboards.</b> The monitoring page frames
/// Grafana, which is the right call — re-drawing 277 panels here would be months of work to
/// arrive somewhere worse. But Grafana is a container like any other, it needs an OIDC round
/// trip that cannot complete in an iframe, and it is one more thing that can be the thing that
/// is broken. An operations console whose only answer in that case is an empty rectangle has
/// failed at the one moment it exists for.</para>
///
/// <para><b>It computes nothing.</b> Every number here is a recording rule the dashboards are
/// already built on — <c>dcms:http_requests:rate5m</c>, <c>dcms:http_errors:rate5m</c>,
/// <c>dcms:http_latency:p95</c> — for the same reason the stores page reads
/// <c>dcms:store_disk_*</c>: arithmetic in two places is two answers that can disagree, and the
/// one in the console would be the one nobody notices is wrong.</para>
///
/// <para><b>Reachability is reported, not inferred.</b> A query that matches nothing and a
/// Prometheus that never answered both produce zero rows, and telling an operator the platform
/// is idle when the truth is that the metrics pipeline is down sends them to debug the wrong
/// thing — the same distinction <c>PrometheusClient</c> is shaped around, and the one ADR 0008
/// was written about.</para>
/// </summary>
public static class PlatformHealthEndpoints
{
    public sealed record ServiceHealth(
        string Service,
        double RequestsPerSecond,
        double ErrorsPerSecond,
        double ErrorRatio,
        double? P95Seconds);

    /// <param name="Up">
    /// False means Prometheus tried to scrape it and could not — which is a different and much
    /// louder fact than a target it has never heard of.
    /// </param>
    public sealed record TargetHealth(string Job, string Instance, bool Up);

    public sealed record HealthResponse(
        bool Reachable,
        IReadOnlyList<ServiceHealth> Services,
        IReadOnlyList<TargetHealth> Targets);

    public static IEndpointRouteBuilder MapPlatformHealthEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/platform/health/signals", async (
            PrometheusClient prometheus, CancellationToken ct) =>
        {
            // Four instant queries rather than one per service: Prometheus returns every
            // service's series in one response, and a console that makes a round trip per row
            // is slower than the platform it is watching.
            // Four instant queries rather than one per service: Prometheus returns every
            // service's series in one response, and a console that makes a round trip per row
            // is slower than the platform it is watching.
            var (reachable, requests) = await prometheus.QueryAsync("dcms:http_requests:rate5m", ct);
            var (_, errors) = await prometheus.QueryAsync("dcms:http_errors:rate5m", ct);
            var (_, latency) = await prometheus.QueryAsync("dcms:http_latency:p95", ct);
            var (_, targets) = await prometheus.QueryAsync("up", ct);

            return Results.Ok(Shape(reachable, requests, errors, latency, targets));
        })
        .RequirePlatformPermission(PlatformConsolePermissions.ObservabilityRead)
        .WithName("PlatformHealthSignals");

        return app;
    }

    /// <summary>
    /// Joins the four query results into one answer per service.
    ///
    /// <para>Separated from the endpoint because the joins are where this is wrong in ways
    /// nothing reports: a ratio taken over no traffic, a p95 of nothing rendered as zero, a
    /// service that appears in one rule's labels and not another's.</para>
    /// </summary>
    public static HealthResponse Shape(
        bool reachable,
        IReadOnlyList<(IReadOnlyDictionary<string, string> Labels, double Value)> requests,
        IReadOnlyList<(IReadOnlyDictionary<string, string> Labels, double Value)> errors,
        IReadOnlyList<(IReadOnlyDictionary<string, string> Labels, double Value)> latency,
        IReadOnlyList<(IReadOnlyDictionary<string, string> Labels, double Value)> targets)
    {
        var byService = new Dictionary<string, ServiceHealth>(StringComparer.Ordinal);

        foreach (var (labels, value) in requests)
        {
            var name = labels.GetValueOrDefault("service", "");
            if (name.Length == 0) continue;
            byService[name] = new ServiceHealth(name, value, 0, 0, null);
        }

        foreach (var (labels, value) in errors)
        {
            var name = labels.GetValueOrDefault("service", "");
            if (name.Length == 0) continue;

            // A service reporting errors with no request rate means the two rules disagree
            // about its labels, which is worth showing rather than dropping.
            var existing = byService.GetValueOrDefault(name) ?? new ServiceHealth(name, 0, 0, 0, null);

            byService[name] = existing with
            {
                ErrorsPerSecond = value,
                // Recomputed here rather than reading dcms:http_error_ratio:rate5m: that rule
                // clamps its denominator to keep a NaN out of an alert expression, which turns
                // an idle service's ratio into an enormous number instead of no answer at all.
                // A console can say "no traffic" honestly.
                ErrorRatio = existing.RequestsPerSecond > 0 ? value / existing.RequestsPerSecond : 0,
            };
        }

        foreach (var (labels, value) in latency)
        {
            var name = labels.GetValueOrDefault("service", "");
            if (name.Length == 0 || !byService.TryGetValue(name, out var existing)) continue;

            // A p95 over no requests is NaN. Reported as null, which reads as "no answer" —
            // which is what it is. Never zero: a p95 of nothing is not fast.
            byService[name] = existing with { P95Seconds = double.IsFinite(value) ? value : null };
        }

        var targetRows = targets
            .Select(t => new TargetHealth(
                t.Labels.GetValueOrDefault("job", "unknown"),
                t.Labels.GetValueOrDefault("instance", "unknown"),
                t.Value >= 1))
            // Down first: this list exists to be scanned for the thing that is broken.
            .OrderBy(t => t.Up)
            .ThenBy(t => t.Job, StringComparer.Ordinal)
            .ToList();

        return new HealthResponse(
            reachable,
            [.. byService.Values
                .OrderByDescending(s => s.ErrorRatio)
                .ThenBy(s => s.Service, StringComparer.Ordinal)],
            targetRows);
    }
}
