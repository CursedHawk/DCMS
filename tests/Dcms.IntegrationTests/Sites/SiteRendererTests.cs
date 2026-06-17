extern alias SiteBuilderApp;
using System.Text;
using System.Text.Json;
using Dcms.Shared.Data.Sites;
using HydrateRuntime = SiteBuilderApp::Dcms.SiteBuilder.Runtime.HydrateRuntime;
using SiteRenderer = SiteBuilderApp::Dcms.SiteBuilder.SiteRenderer;

namespace Dcms.IntegrationTests.Sites;

/// <summary>Pure renderer test — no infrastructure, runs everywhere.</summary>
public class SiteRendererTests
{
    [Fact]
    public void Renders_pages_to_html_with_seo_layout_and_binding_placeholders()
    {
        var definition = JsonSerializer.Deserialize<SiteDefinition>(
            """
            {
              "version": 1,
              "theme": { "colors": { "primary": "#0f172a" }, "fonts": {} },
              "nav": [{ "label": "Home", "path": "/" }],
              "pages": [{
                "id": "home", "path": "/", "title": "Home",
                "seo": { "title": "Welcome to Acme", "description": "Acme home" },
                "root": {
                  "id": "root", "type": "Section", "props": {}, "bindings": [],
                  "children": [
                    { "id": "h", "type": "Hero", "props": { "title": "Big Hero", "subtitle": "Tagline" }, "bindings": [], "children": [] },
                    { "id": "t", "type": "Text", "props": { "text": "Hello world", "variant": "p" }, "bindings": [], "children": [] },
                    { "id": "b", "type": "BlogList", "props": {}, "children": [],
                      "bindings": [{ "propPath": "items", "source": { "instanceSlug": "news", "query": {} } }] }
                  ]
                }
              }]
            }
            """, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;

        var pages = new SiteRenderer().Render(definition);

        pages.Should().HaveCount(1);
        var html = pages[0].Html;
        pages[0].FileName.Should().Be("index.html");
        html.Should().Contain("<title>Welcome to Acme</title>");
        html.Should().Contain("Big Hero");
        html.Should().Contain("Hello world");
        html.Should().Contain("dcms-nav");
        // The data-bound component renders as a hydration placeholder.
        html.Should().Contain("data-dcms-component=\"BlogList\"");
        html.Should().Contain("news");
    }

    [Fact]
    public void Binding_placeholder_carries_props_query_and_references_hydrate_script()
    {
        var definition = JsonSerializer.Deserialize<SiteDefinition>(
            """
            {
              "version": 1, "theme": { "colors": {}, "fonts": {} }, "nav": [],
              "pages": [{
                "id": "home", "path": "/", "title": "Home", "seo": { "title": "Home" },
                "root": {
                  "id": "root", "type": "Section", "props": {}, "bindings": [], "children": [
                    { "id": "b", "type": "BlogList", "props": { "heading": "Latest" }, "children": [],
                      "bindings": [{ "propPath": "items",
                        "source": { "instanceSlug": "blog", "query": { "contentType": "post", "pageSize": 6 } } }] }
                  ]
                }
              }]
            }
            """, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;

        var html = new SiteRenderer().Render(definition)[0].Html;

        // The script tag must be present and hydrate.js must build the delivery URL,
        // so the placeholder needs the contentType (in the query) and the heading prop.
        html.Should().Contain("/_dcms/hydrate.js");
        html.Should().Contain("data-dcms-props=");
        html.Should().Contain("contentType");   // survives serialization (was previously dropped)
        html.Should().Contain("post");
        html.Should().Contain("Latest");
    }

    [Fact]
    public void Hydrate_runtime_is_embedded_and_non_empty()
    {
        HydrateRuntime.Bytes.Should().NotBeEmpty();
        Encoding.UTF8.GetString(HydrateRuntime.Bytes).Should().Contain("data-dcms-component");
    }

    [Theory]
    [InlineData("/", "index.html")]
    [InlineData("/about", "about.html")]
    [InlineData("/blog/post", "blog_post.html")]
    public void Maps_page_paths_to_file_names(string path, string expected)
        => SiteRenderer.FileNameFor(path).Should().Be(expected);
}
