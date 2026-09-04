using System.Net.Http.Json;
using System.Text.Json;

namespace Dcms.PlatformApi.Observability;

public sealed record LokiDeleteRequest(
    string RequestId,
    string Query,
    DateTimeOffset StartTime,
    DateTimeOffset EndTime,
    string Status);

/// <summary>
/// Loki's delete-request API — the one real "delete these logs" primitive on this platform.
///
/// <para>Prometheus can drop series and Tempo cannot drop anything, so this is where a log
/// purge actually happens. It works because <c>loki.yaml</c> already sets
/// <c>retention_enabled</c> and <c>delete_request_store: filesystem</c>; no config change was
/// needed to turn it on, which is worth knowing because it means it has been available
/// unguarded to anything on the compose network all along.</para>
/// </summary>
public sealed class LokiClient(HttpClient http)
{
    /// <summary>
    /// Streams held for 90 days as an integrity control, per <c>loki.yaml</c>'s
    /// <c>retention_stream</c> rules. The Critical <c>AUDIT ANCHOR</c> lines are the third
    /// audit-integrity control the runbook names; deleting them is not a retention decision.
    /// </summary>
    public static readonly IReadOnlyList<string> ProtectedCategories = ["audit", "security"];

    /// <summary>
    /// Appends the exclusions that make a selector provably unable to reach protected streams.
    ///
    /// <para>A <c>!=</c> matcher in LogQL also matches streams where the label is <b>absent</b>,
    /// which is what makes this both safe and usable. Requiring the caller to pin
    /// <c>category</c> instead would have been safe and useless: logs scraped from container
    /// stdout carry only <c>container</c>, <c>stream</c> and <c>service</c> — no category at
    /// all — so nothing an operator actually wants to purge could ever have been named.</para>
    ///
    /// <para>The narrowing is not silent. The effective selector is returned to the caller and
    /// shown in the console, because a purge that quietly did something other than what was
    /// asked is the failure this whole guard exists to avoid.</para>
    /// </summary>
    public static string Guard(string selector)
    {
        var trimmed = selector.Trim();
        if (!trimmed.StartsWith('{') || !trimmed.EndsWith('}'))
        {
            throw new ArgumentException("A stream selector must be wrapped in braces, e.g. {service=\"admin-api\"}.");
        }

        var inner = trimmed[1..^1].Trim();
        if (inner.Length == 0)
        {
            throw new ArgumentException("Refusing an empty selector: {} would match every stream in Loki.");
        }

        var exclusions = string.Join(", ", ProtectedCategories.Select(c => $"category!=\"{c}\""));
        return $"{{{inner}, {exclusions}}}";
    }

    /// <summary>
    /// True when the caller is aiming AT protected data rather than merely near it.
    ///
    /// <para>Guarded separately from <see cref="Guard"/> because the two answer different
    /// questions. Guard makes any selector safe; this refuses the request outright, so an
    /// operator who typed <c>category="audit"</c> is told no rather than handed a silently
    /// empty result they might read as "there was nothing there".</para>
    /// </summary>
    public static bool TargetsProtectedStream(string selector) =>
        ProtectedCategories.Any(c =>
            selector.Contains($"category=\"{c}\"", StringComparison.OrdinalIgnoreCase)
            || selector.Contains($"category=~\"{c}\"", StringComparison.OrdinalIgnoreCase));

    public async Task<string> CreateDeleteRequestAsync(
        string guardedSelector, DateTimeOffset start, DateTimeOffset end, CancellationToken ct)
    {
        var url = $"/loki/api/v1/delete?query={Uri.EscapeDataString(guardedSelector)}"
                  + $"&start={start.ToUnixTimeSeconds()}&end={end.ToUnixTimeSeconds()}";

        using var response = await http.PostAsync(url, content: null, ct);
        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(ct);
            throw new InvalidOperationException(
                $"Loki refused the delete request ({(int)response.StatusCode}): {body}");
        }

        // Loki answers 204 with no body; the request is identified by listing afterwards.
        return await response.Content.ReadAsStringAsync(ct);
    }

    public async Task<IReadOnlyList<LokiDeleteRequest>> ListDeleteRequestsAsync(CancellationToken ct)
    {
        using var response = await http.GetAsync("/loki/api/v1/delete", ct);
        if (!response.IsSuccessStatusCode)
        {
            return [];
        }

        var doc = await response.Content.ReadFromJsonAsync<JsonElement>(ct);
        if (doc.ValueKind != JsonValueKind.Array) return [];

        var rows = new List<LokiDeleteRequest>();
        foreach (var item in doc.EnumerateArray())
        {
            rows.Add(new LokiDeleteRequest(
                item.TryGetProperty("request_id", out var id) ? id.GetString() ?? "" : "",
                item.TryGetProperty("query", out var q) ? q.GetString() ?? "" : "",
                Seconds(item, "start_time"),
                Seconds(item, "end_time"),
                item.TryGetProperty("status", out var s) ? s.GetString() ?? "unknown" : "unknown"));
        }
        return rows;
    }

    /// <summary>
    /// Cancels a delete request that has not yet been processed.
    ///
    /// <para>This is the whole reason <c>retention_delete_delay: 2h</c> is worth surfacing in
    /// the console: for two hours a mistaken purge is undoable, and a safety net nobody is told
    /// about is not a safety net.</para>
    /// </summary>
    public async Task CancelDeleteRequestAsync(string requestId, CancellationToken ct)
    {
        using var response = await http.DeleteAsync(
            $"/loki/api/v1/delete?request_id={Uri.EscapeDataString(requestId)}", ct);
        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(ct);
            throw new InvalidOperationException(
                $"Loki refused to cancel {requestId} ({(int)response.StatusCode}): {body}");
        }
    }

    private static DateTimeOffset Seconds(JsonElement item, string name) =>
        item.TryGetProperty(name, out var v) && v.TryGetInt64(out var unix)
            ? DateTimeOffset.FromUnixTimeSeconds(unix)
            : default;
}
