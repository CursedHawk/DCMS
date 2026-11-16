using System.Text.Json;

namespace Dcms.Shared.Data.Sites;

/// <summary>
/// One page a build publishes, for the sitemap site-host makes (#343). A detail route
/// (<c>/events/:slug</c>) names the plugin content it shows, and is listed once per published
/// item; one that names none (a Dynamic Apps table, a connection) is left out.
/// </summary>
public sealed record SitemapPage(string Path, string? Instance = null, string? ContentType = null)
{
    public bool IsDetail => Path.Contains(':');
}

public static class SitemapPages
{
    /// <summary>Where a build keeps its page list, beside its other <c>_dcms/</c> files.</summary>
    public const string Key = "_dcms/pages.json";

    /// <summary>
    /// Set by the edge on a gated site's <c>/sitemap.xml</c>: the escaped path prefixes a visitor
    /// must sign in for, comma separated, so the sitemap leaves those pages out. Scrubbed from
    /// clients — though a forged one could only hide pages from its sender.
    /// </summary>
    public const string GatedPrefixesHeader = "X-Dcms-Site-Gated";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static byte[] Serialize(IEnumerable<SitemapPage> pages) =>
        JsonSerializer.SerializeToUtf8Bytes(pages.DistinctBy(p => p.Path, StringComparer.OrdinalIgnoreCase).ToList(), Json);

    public static IReadOnlyList<SitemapPage> Parse(ReadOnlySpan<byte> json) =>
        JsonSerializer.Deserialize<List<SitemapPage>>(json, Json) ?? [];
}
