using System.Xml.Linq;
using Dcms.Shared.Data.Sites;
using Dcms.SiteHost;

namespace Dcms.UnitTests.SiteHost;

/// <summary>A hosted site's made sitemap.xml (#343): absolute, items expanded, gated pages left out.</summary>
public class SitemapXmlTests
{
    private static readonly XNamespace Ns = "http://www.sitemaps.org/schemas/sitemap/0.9";

    private static async IAsyncEnumerable<(string, DateTimeOffset?)> Events()
    {
        yield return ("summer-jam", new DateTimeOffset(2026, 7, 1, 18, 30, 0, TimeSpan.FromHours(2)));
        yield return ("winter session", null);
        await Task.CompletedTask;
    }

    private static Task<List<(string Loc, string? LastMod)>> UrlsAsync(IReadOnlyList<SitemapPage> pages, params string[] gated) =>
        UrlsAsync(pages, SiteSearchSettings.Default, gated);

    private static async Task<List<(string Loc, string? LastMod)>> UrlsAsync(
        IReadOnlyList<SitemapPage> pages, SiteSearchSettings search, params string[] gated)
    {
        var xml = await SitemapXml.WriteAsync("https://corp.example", pages, gated,
            page => page.ContentType == "event" ? Events() : AsyncEnumerable.Empty<(string, DateTimeOffset?)>(), search, CancellationToken.None);
        var doc = XDocument.Load(new MemoryStream(xml));
        doc.Root!.Name.Should().Be(Ns + "urlset");
        return doc.Root.Elements(Ns + "url")
            .Select(u => (u.Element(Ns + "loc")!.Value, u.Element(Ns + "lastmod")?.Value)).ToList();
    }

    [Fact]
    public async Task Pages_are_absolute_and_a_detail_page_is_one_url_per_published_item()
    {
        var urls = await UrlsAsync([new("/"), new("/about"), new("/events/:slug", "events", "event"), new("/deals/:id")]);

        urls.Should().Equal(
            ("https://corp.example/", null),
            ("https://corp.example/about", null),
            ("https://corp.example/events/summer-jam", "2026-07-01T16:30:00Z"),
            ("https://corp.example/events/winter%20session", null));
    }

    [Fact]
    public async Task Pages_a_visitor_must_sign_in_for_are_left_out()
    {
        var urls = await UrlsAsync([new("/"), new("/portal"), new("/Portal/x"), new("/portals"), new("/events/:slug", "events", "event")],
            "/portal", "/events/summer-jam");

        urls.Select(u => u.Loc).Should().Equal(
            "https://corp.example/", "https://corp.example/portals", "https://corp.example/events/winter%20session");
    }

    [Fact]
    public void The_edges_header_is_read_back_into_prefixes() =>
        SitemapXml.Prefixes("%2Fportal, %2Fa%2Cb").Should().Equal("/portal", "/a,b");

    [Fact]
    public async Task The_sites_own_rules_hide_pages_and_add_others()
    {
        var pages = new SitemapPage[] { new("/"), new("/about"), new("/drafts/one"), new("/events/:slug", "events", "event") };

        var urls = await UrlsAsync(pages, new SiteSearchSettings
        {
            Exclude = ["/drafts/*", "/events/summer-jam"],
            Include = ["/landing", "/about", "/portal/secret"],
        }, "/portal");

        urls.Select(u => u.Loc).Should().Equal(
            "https://corp.example/", "https://corp.example/about", "https://corp.example/events/winter%20session",
            "https://corp.example/landing");
    }

    [Fact]
    public async Task A_rule_over_a_whole_detail_route_lists_none_of_its_items()
    {
        var listed = false;
        async IAsyncEnumerable<(string, DateTimeOffset?)> Watched()
        {
            listed = true;
            await foreach (var item in Events()) yield return item;
        }
        var xml = await SitemapXml.WriteAsync("https://corp.example", [new("/"), new("/events/:slug", "events", "event")], [],
            _ => Watched(), new SiteSearchSettings { Exclude = ["/events/:slug"] }, CancellationToken.None);

        XDocument.Load(new MemoryStream(xml)).Root!.Elements(Ns + "url").Should().ContainSingle();
        listed.Should().BeFalse("content-api is not asked for items no URL will list");
    }
}
