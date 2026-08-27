using System.Diagnostics;

namespace Dcms.Shared.Telemetry;

/// <summary>
/// Spans for work that is not an HTTP request and not a database call, and would otherwise
/// appear in a trace as an unexplained gap: a transcode, a site build, a model call, a
/// message handler.
///
/// <para>One source for the whole platform rather than one per service. The service is already
/// on every span as the <c>service.name</c> resource attribute, so a second axis saying the
/// same thing would only mean <c>AddSource</c> has to be kept in step with a list of names —
/// and a source nobody remembered to add is a silently missing span, the same failure the
/// audit meter had.</para>
/// </summary>
public static class DcmsActivitySource
{
    public const string Name = "Dcms.Platform";

    private static readonly ActivitySource Source = new(Name, Version);

    private const string Version = "1.0.0";

    /// <summary>
    /// Starts a span, or returns null when nothing is listening — which is the normal state in
    /// tests and under a bare <c>dotnet run</c>. Callers must therefore treat the result as
    /// optional; <c>using var activity = ...</c> handles a null correctly.
    /// </summary>
    public static Activity? Start(string name, ActivityKind kind = ActivityKind.Internal) =>
        Source.StartActivity(name, kind);

    /// <summary>
    /// Starts a span that continues a trace begun in another process — the consumer half of a
    /// JetStream hop, where the parent came in on a <c>traceparent</c> header.
    /// </summary>
    public static Activity? StartLinked(string name, ActivityContext parent, ActivityKind kind = ActivityKind.Consumer) =>
        Source.StartActivity(name, kind, parent);
}
