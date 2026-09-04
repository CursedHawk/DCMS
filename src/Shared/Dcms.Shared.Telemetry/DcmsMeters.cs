using Dcms.Shared.Audit;

namespace Dcms.Shared.Telemetry;

/// <summary>
/// Every meter this platform emits, in one list, because the SDK drops the ones nobody named.
///
/// <para>This exists because of a bug that shipped and survived a release. <c>AuditMetrics</c>
/// defined six instruments and <c>docs/runbook.md</c> documented them as exported, with an
/// alert table built on them. They were never exported: OpenTelemetry requires each meter to be
/// subscribed by name with <c>AddMeter</c>, that call was missing, and the SDK filters out an
/// unsubscribed meter without an error, a warning or a log line.</para>
///
/// <para>What made it survive is worth stating plainly, because it is the reason for this file
/// rather than a one-line fix at the call site: the headline guidance for
/// <c>dcms.audit.recorded</c> is that <b>flat-lining is the alarm</b>. A meter that exports
/// nothing and a platform on which nobody did anything produce byte-identical output. There was
/// no symptom to notice.</para>
///
/// <para>So: add a meter, add it here. <c>DcmsMeterRegistrationTests</c> reflects over the
/// solution and fails the build if a <c>MeterName</c> constant exists that this list does not
/// carry.</para>
/// </summary>
public static class DcmsMeters
{
    /// <summary>Meters defined by this platform's own code.</summary>
    public static readonly string[] Dcms =
    [
        AuditMetrics.MeterName,
        DcmsMetrics.MeterName,
    ];

    /// <summary>
    /// Meters the framework and its libraries publish. Named explicitly for the same reason:
    /// none of them export unless asked.
    /// </summary>
    public static readonly string[] Framework =
    [
        "Microsoft.AspNetCore.Hosting",
        "Microsoft.AspNetCore.Server.Kestrel",
        "Microsoft.AspNetCore.RateLimiting",
        "Microsoft.AspNetCore.Routing",
        "Microsoft.AspNetCore.Diagnostics",
        "System.Net.Http",
        "System.Runtime",
        "Npgsql",
        // The edge's own proxy metrics: requests forwarded, failures by reason, and the
        // per-destination counters that say which upstream is degrading. Without this line
        // the edge exports Kestrel and ASP.NET counters and nothing about proxying — the
        // same shape of gap as the audit meter that survived a release.
        "Yarp.ReverseProxy",
    ];

    public static readonly string[] All = [.. Dcms, .. Framework];
}
