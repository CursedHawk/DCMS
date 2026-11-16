using System.Text;
using System.Xml;
using Dcms.Shared.Data.Sites;

namespace Dcms.SiteHost;

/// <summary>A site's sitemap.xml from its build's page list (#343): absolute, item pages expanded.</summary>
public static class SitemapXml
{
    /// <summary>The protocol's cap on one sitemap file.</summary>
    public const int MaxUrls = 50_000;

    private const string Namespace = "http://www.sitemaps.org/schemas/sitemap/0.9";

    /// <param name="baseUrl">Scheme and host the sitemap was asked on, without a trailing slash.</param>
    /// <param name="gated">Path prefixes a visitor must sign in for; pages under them are left out.</param>
    /// <param name="items">A detail page's published items, slug and when.</param>
    /// <param name="search">The site's own say (#648): pages it hides, and pages it adds.</param>
    public static async Task<byte[]> WriteAsync(
        string baseUrl, IReadOnlyList<SitemapPage> pages, IReadOnlyList<string> gated,
        Func<SitemapPage, IAsyncEnumerable<(string Slug, DateTimeOffset? PublishedAt)>> items,
        SiteSearchSettings search, CancellationToken ct)
    {
        using var buffer = new MemoryStream();
        await using (var xml = XmlWriter.Create(buffer, new XmlWriterSettings { Async = true, Encoding = new UTF8Encoding(false) }))
        {
            await xml.WriteStartDocumentAsync();
            await xml.WriteStartElementAsync(null, "urlset", Namespace);
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            async Task UrlAsync(string path, DateTimeOffset? lastModified)
            {
                if (seen.Count >= MaxUrls || Covered(path, gated) || search.Excludes(path) || !seen.Add(path)) return;
                var escaped = string.Join('/', path.Split('/').Select(Uri.EscapeDataString));
                await xml.WriteStartElementAsync(null, "url", Namespace);
                await xml.WriteElementStringAsync(null, "loc", Namespace, baseUrl + (escaped.Length == 0 ? "/" : escaped));
                if (lastModified is { } at)
                {
                    await xml.WriteElementStringAsync(null, "lastmod", Namespace, at.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'"));
                }
                await xml.WriteEndElementAsync();
            }

            foreach (var page in pages)
            {
                if (seen.Count >= MaxUrls) break;
                if (!page.IsDetail)
                {
                    await UrlAsync(page.Path, null);
                    continue;
                }
                // A rule over the whole route (/blog/:slug, /blog/*) spares listing its items at all.
                if (page.Instance is null || page.ContentType is null || Covered(page.Path, gated) || search.Excludes(page.Path)) continue;
                var folder = page.Path[..page.Path.LastIndexOf('/')];
                await foreach (var (slug, publishedAt) in items(page).WithCancellation(ct))
                {
                    if (seen.Count >= MaxUrls) break;
                    await UrlAsync($"{folder}/{slug}", publishedAt);
                }
            }
            foreach (var path in search.Include)
            {
                await UrlAsync(path, null);
            }
            await xml.WriteEndElementAsync();
            await xml.WriteEndDocumentAsync();
        }
        return buffer.ToArray();
    }

    /// <summary>The prefixes the edge sends in <see cref="SitemapPages.GatedPrefixesHeader"/>.</summary>
    public static IReadOnlyList<string> Prefixes(string? header) =>
        string.IsNullOrEmpty(header) ? []
            : header.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(Uri.UnescapeDataString).ToList();

    /// <summary>
    /// Whether a gate rule's prefix covers the path, as the edge's <c>SiteGateRules</c> compares:
    /// without case, <c>/portal</c> covering <c>/portal/x</c> and not <c>/portals</c>.
    /// </summary>
    // ponytail: every non-public prefix excludes, ignoring rule order — a public rule nested under
    // a gated one drops its pages too. Ask the edge's Decide per path if that ever matters.
    public static bool Covered(string path, IReadOnlyList<string> gated) =>
        gated.Any(prefix =>
        {
            var p = prefix.TrimEnd('/');
            return p.Length == 0 || path.Equals(p, StringComparison.OrdinalIgnoreCase)
                || path.StartsWith(p + "/", StringComparison.OrdinalIgnoreCase);
        });
}
