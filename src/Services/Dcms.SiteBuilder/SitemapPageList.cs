using System.Text.Json;
using Dcms.Shared.Data.Sites;

namespace Dcms.SiteBuilder;

/// <summary>
/// The pages a Mode D build lists for its sitemap (#343), stored as <see cref="SitemapPages.Key"/>;
/// site-host makes <c>sitemap.xml</c> from it on request, under whichever domain was asked and with
/// each detail page's published items. Every other kind of site lists only what its people enter
/// in the console (#648).
/// </summary>
public static class SitemapPageList
{
    /// <summary>
    /// Mode D: <c>dcms/app.json</c>'s routes less noindex pages; a detail route by its page's plugin
    /// source. Without that file, just the root.
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

    /// <summary>site-host only wildcards the last segment, so <c>/a/:x/b</c> never resolves.</summary>
    private static bool DetailParamIsLast(string path)
    {
        var segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        return segments.Length > 0 && segments[^1].StartsWith(':') && !segments[..^1].Any(s => s.StartsWith(':'));
    }

    private static string? Text(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() : null;
}
