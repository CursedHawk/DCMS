extern alias SiteBuilderApp;
using Dcms.Shared.Data.Sites;
using SitemapPageList = SiteBuilderApp::Dcms.SiteBuilder.SitemapPageList;

namespace Dcms.IntegrationTests.Sites;

/// <summary>What a Mode D build lists for its sitemap (#343); every other site's is entered by hand (#648).</summary>
public class SitemapPageListTests
{
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
    public void A_visual_app_without_its_documents_lists_its_root() =>
        SitemapPageList.ForViteApp(new Dictionary<string, string> { ["src/App.tsx"] = "" }).Should().Equal(new SitemapPage("/"));
}
