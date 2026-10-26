namespace Dcms.Shared.Kernel.Abstractions;

/// <summary>
/// Marks the requests a site preview makes through the admin preview proxy (ADR 0026).
///
/// <para>Nothing is partitioned by it: a preview acts on its environment's live data, the way
/// the site does. It only keeps editors looking at their own work out of the site's analytics.
/// The edge strips it from public requests, like every internal header.</para>
/// </summary>
public static class PreviewRequest
{
    public const string Header = "X-Dcms-Preview";
}
