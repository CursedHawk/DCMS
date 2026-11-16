using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using Dcms.Shared.Data.Sites;

namespace Dcms.SiteBuilder;

/// <summary>
/// The pages each kind of build lists for its sitemap (#343), stored as
/// <see cref="SitemapPages.Key"/>; site-host makes <c>sitemap.xml</c> from it on request, under
/// whichever domain was asked and with each detail page's published items.
/// </summary>
public static partial class SitemapPageList
{
    /// <summary>
    /// Mode A: the manifest's pages less those marked noindex, the home page as <c>/</c>. A detail
    /// page is listed by the item binding in its markup (<c>"propPath":"item"</c>), or not at all.
    /// </summary>
    public static List<SitemapPage> ForStaticSite(IReadOnlyDictionary<string, string> files)
    {
        if (SiteManifest.TryParse(files.GetValueOrDefault(StaticSiteAssembler.SiteJson)) is not { } manifest)
        {
            return [];
        }
        var home = manifest.HomePage();
        var pages = new List<SitemapPage>();
        foreach (var page in manifest.Pages.Where(p => !p.Seo.NoIndex))
        {
            if (page == home)
            {
                pages.Add(new SitemapPage("/"));
            }
            else if (!StaticSiteAssembler.IsDetailRoute(page.Path))
            {
                pages.Add(new SitemapPage(page.Path));
            }
            else if (DetailParamIsLast(page.Path)
                     && ItemBinding(files.GetValueOrDefault(StaticSiteAssembler.PageHtmlPath(page.Slug), string.Empty)) is { } source)
            {
                pages.Add(new SitemapPage(page.Path, source.Instance, source.ContentType));
            }
        }
        return pages;
    }

    /// <summary>
    /// Mode D: <c>dcms/app.json</c>'s routes less noindex pages; a detail route by its page's plugin
    /// source. Mode B has no such file and nothing to read its routes from: just its root.
    /// </summary>
    public static List<SitemapPage> ForViteApp(IReadOnlyDictionary<string, string> files)
    {
        if (files.GetValueOrDefault("dcms/app.json") is not { } appJson)
        {
            return [new SitemapPage("/")];
        }
        var pages = new List<SitemapPage>();
        try
        {
            using var app = JsonDocument.Parse(appJson);
            if (!app.RootElement.TryGetProperty("routes", out var routes) || routes.ValueKind != JsonValueKind.Array)
            {
                return [new SitemapPage("/")];
            }
            foreach (var route in routes.EnumerateArray())
            {
                if (Text(route, "path") is not { } path || Text(route, "page") is not { } pageId
                    || files.GetValueOrDefault($"dcms/pages/{pageId}.json") is not { } pageJson)
                {
                    continue;
                }
                using var page = JsonDocument.Parse(pageJson);
                var root = page.RootElement;
                if (root.TryGetProperty("seo", out var seo) && seo.TryGetProperty("noIndex", out var noIndex) && noIndex.ValueKind == JsonValueKind.True)
                {
                    continue;
                }
                if (!path.Contains(':'))
                {
                    pages.Add(new SitemapPage(path));
                }
                else if (DetailParamIsLast(path)
                         && root.TryGetProperty("data", out var data) && data.TryGetProperty("source", out var source)
                         && Text(source, "instance") is { } instance && Text(source, "contentType") is { } contentType)
                {
                    pages.Add(new SitemapPage(path, instance, contentType));
                }
            }
        }
        catch (JsonException)
        {
            // The build itself reports a broken document; the sitemap just has less in it.
        }
        return pages.Count == 0 ? [new SitemapPage("/")] : pages;
    }

    /// <summary>Mode C: every HTML file, at the path it is served from as-is.</summary>
    public static List<SitemapPage> ForStaticFiles(IEnumerable<string> paths) =>
        paths.Where(p => p.EndsWith(".html", StringComparison.OrdinalIgnoreCase) || p.EndsWith(".htm", StringComparison.OrdinalIgnoreCase))
            .Where(p => !Path.GetFileName(p).StartsWith("404.", StringComparison.OrdinalIgnoreCase))
            .Select(p => new SitemapPage(string.Equals(p, "index.html", StringComparison.OrdinalIgnoreCase) ? "/" : "/" + p))
            .ToList();

    /// <summary>site-host only wildcards the last segment, so <c>/a/:x/b</c> never resolves.</summary>
    private static bool DetailParamIsLast(string path)
    {
        var segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        return segments.Length > 0 && segments[^1].StartsWith(':') && !segments[..^1].Any(s => s.StartsWith(':'));
    }

    private static string? Text(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() : null;

    [GeneratedRegex("""data-dcms-bindings\s*=\s*(?:"([^"]*)"|'([^']*)')""")]
    private static partial Regex BindingsAttribute();

    /// <summary>The plugin content a detail page shows: its first binding of <c>item</c>.</summary>
    private static (string Instance, string ContentType)? ItemBinding(string html)
    {
        foreach (Match match in BindingsAttribute().Matches(html))
        {
            var raw = WebUtility.HtmlDecode(match.Groups[1].Success ? match.Groups[1].Value : match.Groups[2].Value);
            try
            {
                using var bindings = JsonDocument.Parse(raw);
                if (bindings.RootElement.ValueKind != JsonValueKind.Array) continue;
                foreach (var binding in bindings.RootElement.EnumerateArray())
                {
                    if (Text(binding, "propPath") == "item" && Text(binding, "instanceSlug") is { } instance
                        && binding.TryGetProperty("query", out var query) && Text(query, "contentType") is { } contentType)
                    {
                        return (instance, contentType);
                    }
                }
            }
            catch (JsonException)
            {
                // Not ours to judge here; the page renders it empty all the same.
            }
        }
        return null;
    }
}
