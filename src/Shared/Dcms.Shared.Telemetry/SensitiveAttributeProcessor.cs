using System.Diagnostics;
using Dcms.Shared.Audit.Redaction;
using OpenTelemetry;

namespace Dcms.Shared.Telemetry;

/// <summary>
/// Removes span attributes that look like they hold a secret, and strips the query string from
/// captured URLs.
///
/// <para>A trace store is not access-controlled the way the audit log is: anyone who can open
/// the traces dashboard can read every attribute on every span. Auto-instrumentation is
/// generous — it will happily record a full URL including <c>?token=…</c>, or a database
/// parameter, or a header a library decided to attach — and none of those authors knew about
/// this platform's data. So the default here matches the audit log's: a value is withheld
/// unless it has been looked at.</para>
///
/// <para>The test is <see cref="AuditRedactor.LooksSensitiveName"/>, the same one that
/// withholds an audit field. There is a second, independent copy of this rule in the Alloy
/// collector config, which is not redundancy for its own sake: a service running an older
/// image than the collector still has its spans scrubbed, and that is exactly the window in
/// which a leak would otherwise happen unnoticed.</para>
/// </summary>
public sealed class SensitiveAttributeProcessor : BaseProcessor<Activity>
{
    private const string Redacted = "[redacted]";

    public override void OnEnd(Activity activity)
    {
        // TagObjects is a linked list rebuilt by SetTag, so the removals are collected first
        // rather than mutating mid-enumeration.
        List<string>? sensitive = null;
        string? urlToTrim = null;

        foreach (var tag in activity.TagObjects)
        {
            if (AuditRedactor.LooksSensitiveName(tag.Key))
            {
                (sensitive ??= []).Add(tag.Key);
                continue;
            }

            if (IsUrlTag(tag.Key) && tag.Value is string url && url.Contains('?', StringComparison.Ordinal))
            {
                urlToTrim = tag.Key;
            }
        }

        if (sensitive is not null)
        {
            foreach (var key in sensitive)
            {
                activity.SetTag(key, Redacted);
            }
        }

        if (urlToTrim is not null && activity.GetTagItem(urlToTrim) is string full)
        {
            var cut = full.IndexOf('?', StringComparison.Ordinal);
            activity.SetTag(urlToTrim, full[..cut]);
        }

        base.OnEnd(activity);
    }

    /// <summary>
    /// The two attributes that carry a whole URL. <c>url.path</c> and <c>http.route</c> are
    /// left alone: they never contain a query string, and the route is what every latency
    /// panel groups by.
    /// </summary>
    private static bool IsUrlTag(string key) =>
        key is "url.full" or "http.url";
}
