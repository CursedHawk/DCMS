using System.Text.Json;

namespace Dcms.AdminApi.Sites.Audits;

/// <summary>
/// What a site's people and its agent act on from one PageSpeed Insights run (#649): the four
/// category scores, the lab metrics, real-user (field) data when Google has it, and every audit
/// that failed with the first few things it found. The full Lighthouse report is several
/// megabytes; this keeps what can be fixed.
/// </summary>
public sealed record AuditReport(
    string Strategy,
    string? FinalUrl,
    string? LighthouseVersion,
    IReadOnlyDictionary<string, int?> Scores,
    IReadOnlyList<AuditMetric> Metrics,
    string? FieldCategory,
    IReadOnlyList<FieldMetric> Field,
    IReadOnlyList<AuditIssue> Issues,
    IReadOnlyList<string> Warnings);

public sealed record AuditMetric(string Id, string Title, string? DisplayValue, double? Score);

public sealed record FieldMetric(string Id, double? Percentile, string? Category);

public sealed record AuditIssue(
    string Id, string Category, string Title, string Description, double? Score, string? DisplayValue,
    double? SavingsMs, double? SavingsBytes, IReadOnlyList<AuditItem> Items, int ItemCount);

public sealed record AuditItem(string? Url, string? Selector, string? Snippet, string? Label, double? WastedBytes, double? WastedMs, double? TotalBytes);

public static class AuditSummary
{
    public const int MaxItems = 10;
    private const int MaxText = 300;
    private const int MaxDescription = 1200;

    private static readonly string[] MetricIds =
        ["first-contentful-paint", "largest-contentful-paint", "total-blocking-time", "cumulative-layout-shift", "speed-index"];

    private static readonly HashSet<string> FailingModes = ["binary", "numeric", "metricSavings"];

    /// <summary>The summary of a PSI response (<c>runPagespeed</c>'s JSON).</summary>
    public static AuditReport From(JsonElement psi, string strategy)
    {
        var lighthouse = psi.GetProperty("lighthouseResult");
        var audits = lighthouse.TryGetProperty("audits", out var a) ? a : default;
        var scores = new Dictionary<string, int?>();
        var categoryOf = new Dictionary<string, string>();
        var metricAudits = new HashSet<string>();
        if (lighthouse.TryGetProperty("categories", out var categories))
        {
            foreach (var category in categories.EnumerateObject())
            {
                scores[category.Name] = category.Value.TryGetProperty("score", out var s) && s.ValueKind == JsonValueKind.Number
                    ? (int)Math.Round(s.GetDouble() * 100) : null;
                if (!category.Value.TryGetProperty("auditRefs", out var refs)) continue;
                foreach (var reference in refs.EnumerateArray())
                {
                    var id = Text(reference, "id");
                    if (id is null) continue;
                    categoryOf.TryAdd(id, category.Name);
                    if (Text(reference, "group") == "metrics") metricAudits.Add(id);
                }
            }
        }

        var metrics = MetricIds
            .Where(id => audits.ValueKind == JsonValueKind.Object && audits.TryGetProperty(id, out _))
            .Select(id => audits.GetProperty(id))
            .Select(m => new AuditMetric(Text(m, "id")!, Text(m, "title") ?? "", Text(m, "displayValue"), Number(m, "score")))
            .ToList();

        var issues = new List<AuditIssue>();
        if (audits.ValueKind == JsonValueKind.Object)
        {
            foreach (var entry in audits.EnumerateObject())
            {
                var audit = entry.Value;
                if (metricAudits.Contains(entry.Name) || !categoryOf.TryGetValue(entry.Name, out var category)) continue;
                if (Number(audit, "score") is not { } score || score >= 0.9) continue;
                if (!FailingModes.Contains(Text(audit, "scoreDisplayMode") ?? "")) continue;
                var details = audit.TryGetProperty("details", out var d) ? d : default;
                var items = details.ValueKind == JsonValueKind.Object && details.TryGetProperty("items", out var list) && list.ValueKind == JsonValueKind.Array
                    ? list : default;
                issues.Add(new AuditIssue(
                    entry.Name, category, Text(audit, "title") ?? entry.Name, Clip(Text(audit, "description"), MaxDescription) ?? "",
                    score, Text(audit, "displayValue"),
                    Number(details, "overallSavingsMs"), Number(details, "overallSavingsBytes"),
                    items.ValueKind == JsonValueKind.Array ? items.EnumerateArray().Take(MaxItems).Select(Item).ToList() : [],
                    items.ValueKind == JsonValueKind.Array ? items.GetArrayLength() : 0));
            }
        }
        var order = PageSpeedClient.Categories.ToList();
        issues.Sort((x, y) =>
        {
            var byCategory = order.IndexOf(x.Category).CompareTo(order.IndexOf(y.Category));
            if (byCategory != 0) return byCategory;
            var bySavings = (y.SavingsMs ?? 0).CompareTo(x.SavingsMs ?? 0);
            return bySavings != 0 ? bySavings : (x.Score ?? 0).CompareTo(y.Score ?? 0);
        });

        var field = new List<FieldMetric>();
        string? fieldCategory = null;
        if (psi.TryGetProperty("loadingExperience", out var experience) && experience.TryGetProperty("metrics", out var fieldMetrics))
        {
            fieldCategory = Text(experience, "overall_category");
            field.AddRange(fieldMetrics.EnumerateObject().Select(m => new FieldMetric(m.Name, Number(m.Value, "percentile"), Text(m.Value, "category"))));
        }

        var warnings = lighthouse.TryGetProperty("runWarnings", out var w) && w.ValueKind == JsonValueKind.Array
            ? w.EnumerateArray().Select(x => Clip(x.GetString(), MaxText) ?? "").Where(x => x.Length > 0).ToList()
            : [];
        return new AuditReport(strategy, Text(lighthouse, "finalDisplayedUrl") ?? Text(lighthouse, "finalUrl"),
            Text(lighthouse, "lighthouseVersion"), scores, metrics, fieldCategory, field, issues, warnings);
    }

    private static AuditItem Item(JsonElement item)
    {
        var node = item.ValueKind == JsonValueKind.Object && item.TryGetProperty("node", out var n) ? n : default;
        var source = item.ValueKind == JsonValueKind.Object && item.TryGetProperty("source", out var s) ? s : default;
        return new AuditItem(
            Clip(Text(item, "url") ?? Text(source, "url"), MaxText),
            Clip(Text(node, "selector"), MaxText),
            Clip(Text(node, "snippet"), MaxText),
            Clip(Text(node, "nodeLabel") ?? Text(item, "label"), MaxText),
            Number(item, "wastedBytes"), Number(item, "wastedMs"), Number(item, "totalBytes"));
    }

    private static string? Text(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static double? Number(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : null;

    private static string? Clip(string? value, int max) => value is null ? null : value.Length <= max ? value : value[..max] + "…";
}
