namespace Dcms.SiteBuilder.Runtime;

/// <summary>
/// The script that reads a Mode B site's pages from its React routes (#343), embedded and written
/// into each Mode B build's work dir; see <c>sitemap-routes.mjs</c> and <c>ReactAppBuilder.ListPagesAsync</c>.
/// </summary>
public static class SitemapRuntime
{
    public const string ScriptFile = ".dcms-sitemap.mjs";

    /// <summary>What the script writes: <c>[{ path, instance?, contentType? }]</c>.</summary>
    public const string OutputFile = ".dcms-sitemap.json";

    public static byte[] Bytes { get; } = HydrateRuntime.Load("Dcms.SiteBuilder.Runtime.sitemap-routes.mjs");
}
