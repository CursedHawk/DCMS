using System.Net.Http.Json;
using System.Text.Json;

namespace Dcms.PlatformApi.Observability;

/// <summary>
/// Instant queries against Prometheus, plus the two admin operations that delete series.
///
/// <para>Read and delete live on one type but behind separate methods, and the delete pair is
/// gated on <c>platform:logs:purge</c> at the endpoint. Prometheus itself has no
/// authorization — anything that can reach it on the compose network can do anything — so the
/// control is entirely on this side of the call.</para>
/// </summary>
public sealed class PrometheusClient(HttpClient http, ILogger<PrometheusClient> logger)
{
    /// <summary>
    /// One instant query. <c>Reachable</c> is false only when Prometheus could not answer.
    ///
    /// <para>The distinction is the whole point of the return shape. A query that succeeds and
    /// matches nothing and a query that never reached Prometheus both produce zero rows, and
    /// telling an operator "Prometheus is not answering" when the truth is "the store-usage
    /// sidecar has not reported yet" sends them to debug the wrong thing. This is the same
    /// mistake ADR 0008 was written about: absence of signal is only evidence when the signal
    /// is known to be connected.</para>
    ///
    /// <para>Never throws. The stores page is what someone opens when things are broken, and it
    /// should render what it can rather than fail whole.</para>
    /// </summary>
    public async Task<(bool Reachable, IReadOnlyList<(IReadOnlyDictionary<string, string> Labels, double Value)> Rows)>
        QueryAsync(string query, CancellationToken ct)
    {
        try
        {
            using var response = await http.GetAsync(
                $"/api/v1/query?query={Uri.EscapeDataString(query)}", ct);
            if (!response.IsSuccessStatusCode)
            {
                logger.LogWarning("Prometheus query failed ({Status}): {Query}", response.StatusCode, query);
                return (false, []);
            }

            var doc = await response.Content.ReadFromJsonAsync<JsonElement>(ct);
            if (!doc.TryGetProperty("data", out var data)
                || !data.TryGetProperty("result", out var result)
                || result.ValueKind != JsonValueKind.Array)
            {
                // Answered, but not in a shape this understands. Reachable, no rows.
                return (true, []);
            }

            var rows = new List<(IReadOnlyDictionary<string, string>, double)>();
            foreach (var series in result.EnumerateArray())
            {
                var labels = new Dictionary<string, string>(StringComparer.Ordinal);
                if (series.TryGetProperty("metric", out var metric))
                {
                    foreach (var label in metric.EnumerateObject())
                    {
                        labels[label.Name] = label.Value.GetString() ?? string.Empty;
                    }
                }

                // [ <unix ts>, "<value as string>" ] — the value is a string even for numbers.
                if (series.TryGetProperty("value", out var value)
                    && value.ValueKind == JsonValueKind.Array
                    && value.GetArrayLength() == 2
                    && double.TryParse(value[1].GetString(), System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture, out var parsed))
                {
                    rows.Add((labels, parsed));
                }
            }
            return (true, rows);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Prometheus unreachable for query: {Query}", query);
            return (false, []);
        }
    }

    /// <summary>
    /// Marks series matching <paramref name="matchers"/> for deletion.
    ///
    /// <para>Needs <c>--web.enable-admin-api</c>, which the compose file turns on with the
    /// exposure noted. Deletion alone frees no disk — it writes tombstones — which is why the
    /// endpoint follows it with <see cref="CleanTombstonesAsync"/>. Unlike the read path this
    /// throws: a purge that quietly did nothing is worse than one that reports a failure.</para>
    /// </summary>
    public async Task DeleteSeriesAsync(
        IReadOnlyCollection<string> matchers, DateTimeOffset? start, DateTimeOffset? end, CancellationToken ct)
    {
        var query = new List<string>();
        foreach (var m in matchers)
        {
            query.Add($"match[]={Uri.EscapeDataString(m)}");
        }
        if (start is not null) query.Add($"start={start.Value.ToUnixTimeSeconds()}");
        if (end is not null) query.Add($"end={end.Value.ToUnixTimeSeconds()}");

        using var response = await http.PostAsync(
            $"/api/v1/admin/tsdb/delete_series?{string.Join('&', query)}", content: null, ct);
        await ThrowIfFailedAsync(response, "delete_series", ct);
    }

    public async Task CleanTombstonesAsync(CancellationToken ct)
    {
        using var response = await http.PostAsync("/api/v1/admin/tsdb/clean_tombstones", content: null, ct);
        await ThrowIfFailedAsync(response, "clean_tombstones", ct);
    }

    private static async Task ThrowIfFailedAsync(HttpResponseMessage response, string what, CancellationToken ct)
    {
        if (response.IsSuccessStatusCode) return;

        var body = await response.Content.ReadAsStringAsync(ct);
        // 404 here almost always means one thing, and saying so saves an hour: the admin API is
        // a startup flag, not a permission, so a Prometheus without it answers "not found"
        // rather than "forbidden".
        var hint = response.StatusCode == System.Net.HttpStatusCode.NotFound
            ? " Prometheus was probably started without --web.enable-admin-api."
            : string.Empty;
        throw new InvalidOperationException(
            $"Prometheus {what} failed with {(int)response.StatusCode}.{hint} {body}".Trim());
    }
}
