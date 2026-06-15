extern alias SiteBuilderApp;
using System.Text.Json;
using Dcms.Shared.Data.Sites;
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

    [Theory]
    [InlineData("/", "index.html")]
    [InlineData("/about", "about.html")]
    [InlineData("/blog/post", "blog_post.html")]
    public void Maps_page_paths_to_file_names(string path, string expected)
        => SiteRenderer.FileNameFor(path).Should().Be(expected);
}
