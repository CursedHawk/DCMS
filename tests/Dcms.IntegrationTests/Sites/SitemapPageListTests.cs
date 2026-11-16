extern alias SiteBuilderApp;
using Dcms.Shared.Data.Sites;
using SitemapPageList = SiteBuilderApp::Dcms.SiteBuilder.SitemapPageList;

namespace Dcms.IntegrationTests.Sites;

/// <summary>What each kind of build lists for its sitemap (#343).</summary>
public class SitemapPageListTests
{
    [Fact]
    public void A_static_site_lists_its_pages_and_a_detail_page_by_its_item_binding()
    {
        var files = new Dictionary<string, string>
        {
            ["site.json"] = """
                { "version": 2, "pages": [
                  { "id": "1", "slug": "home", "path": "/home", "title": "Home", "home": true },
                  { "id": "2", "slug": "about", "path": "/about", "title": "About" },
                  { "id": "3", "slug": "draft", "path": "/draft", "title": "Draft", "seo": { "title": "", "noIndex": true } },
                  { "id": "4", "slug": "event", "path": "/events/:slug", "title": "Event" },
                  { "id": "5", "slug": "deal", "path": "/deals/:id", "title": "Deal" } ] }
                """,
            ["pages/event.html"] = """<div data-dcms-component="Events.Event" data-dcms-bindings="[{&quot;propPath&quot;:&quot;item&quot;,&quot;instanceSlug&quot;:&quot;events&quot;,&quot;query&quot;:{&quot;contentType&quot;:&quot;event&quot;}}]"></div>""",
            ["pages/deal.html"] = "<p>No binding.</p>",
        };

        SitemapPageList.ForStaticSite(files).Should().Equal(
            new SitemapPage("/"), new SitemapPage("/about"), new SitemapPage("/events/:slug", "events", "event"));
    }

    [Fact]
    public void A_visual_app_lists_its_routes_and_a_detail_route_by_its_plugin_source()
    {
        var files = new Dictionary<string, string>
        {
            ["dcms/app.json"] = """
                { "routes": [
                  { "id": "home", "path": "/", "page": "home" },
                  { "id": "hidden", "path": "/hidden", "page": "hidden" },
                  { "id": "event", "path": "/events/:slug", "page": "event" },
                  { "id": "deal", "path": "/deals/:id", "page": "deal" } ] }
                """,
            ["dcms/pages/home.json"] = """{ "id": "home" }""",
            ["dcms/pages/hidden.json"] = """{ "id": "hidden", "seo": { "noIndex": true } }""",
            ["dcms/pages/event.json"] = """{ "id": "event", "data": { "source": { "instance": "events", "contentType": "event" }, "param": "slug" } }""",
            ["dcms/pages/deal.json"] = """{ "id": "deal", "data": { "source": { "app": "crm", "table": "deals" }, "param": "id" } }""",
        };

        SitemapPageList.ForViteApp(files).Should().Equal(new SitemapPage("/"), new SitemapPage("/events/:slug", "events", "event"));
    }

    [Fact]
    public void A_react_app_lists_its_root_and_uploaded_files_their_html()
    {
        SitemapPageList.ForViteApp(new Dictionary<string, string> { ["src/App.tsx"] = "" }).Should().Equal(new SitemapPage("/"));
        SitemapPageList.ForStaticFiles(["index.html", "docs/guide.html", "404.html", "app.js", "docs/index.htm"])
            .Should().Equal(new SitemapPage("/"), new SitemapPage("/docs/guide.html"), new SitemapPage("/docs/index.htm"));
    }
    [Fact]
    public void A_react_apps_pages_from_its_routes_are_checked_like_any_input()
    {
        var json = """
            [
              { "path": "/" },
              { "path": "/about" },
              { "path": "/blog/:slug", "instance": "blog", "contentType": "post" },
              { "path": "relative" },
              { "path": "/a/../b" },
              { "path": "/a//b" },
              { "path": "/tab\there" },
              { "path": "/half/:slug", "instance": "blog" },
              { "path": "/bad/:slug", "instance": "a b", "contentType": "post" },
              { "path": "/mid/:x/tail", "instance": "blog", "contentType": "post" },
              { "path": 42 },
              "nonsense"
            ]
            """;

        SitemapPageList.FromRoutesJson(json).Should().Equal(
            new SitemapPage("/"), new SitemapPage("/about"), new SitemapPage("/blog/:slug", "blog", "post"));
        SitemapPageList.FromRoutesJson("""{ "path": "/" }""").Should().BeEmpty();
        SitemapPageList.FromRoutesJson("[" + string.Join(',', Enumerable.Range(0, 50_010).Select(i => $$"""{"path":"/p{{i}}"}""")) + "]")
            .Should().HaveCount(50_000);
    }
}
