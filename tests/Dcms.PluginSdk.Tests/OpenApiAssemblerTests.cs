using System.Text.Json;
using Dcms.PluginSdk.Abstractions;
using Dcms.PluginSdk.Runtime;
using Dcms.Plugins.Analytics;
using Dcms.Plugins.Articles;
using Dcms.Plugins.Blog;

namespace Dcms.PluginSdk.Tests;

public class OpenApiAssemblerTests
{
    private static PluginInstanceContext Instance(string pluginId, string slug, string name, string description)
        => new(Guid.NewGuid(), Guid.NewGuid(), pluginId, slug, name, description, JsonDocument.Parse("{}"));

    [Fact]
    public void Assembles_paths_tags_and_injects_instance_description_into_operations()
    {
        var registry = new PluginRegistry([new ArticlesPlugin(), new BlogPlugin()]);
        var assembler = new OpenApiAssembler(registry);

        var doc = assembler.Build("Acme", [
            Instance("articles", "news", "Company News", "Official Acme announcements."),
            Instance("blog", "devblog", "Dev Blog", "Engineering deep dives."),
        ]);

        var paths = doc["paths"]!.AsObject();
        paths.Should().ContainKey("/api/news/article");
        paths.Should().ContainKey("/api/news/article/{slug}");
        paths.Should().ContainKey("/api/devblog/post");

        // The admin-authored instance description flows into operation descriptions.
        var listDescription = paths["/api/news/article"]!["get"]!["description"]!.GetValue<string>();
        listDescription.Should().Contain("Official Acme announcements.");
        listDescription.Should().Contain("Plugin: Articles");

        var tagNames = doc["tags"]!.AsArray().Select(t => t!["name"]!.GetValue<string>()).ToList();
        tagNames.Should().Contain(["Company News", "Dev Blog"]);

        doc["info"]!["title"]!.GetValue<string>().Should().Be("Acme Content API");
    }

    [Fact]
    public void Types_the_item_data_schema_from_the_content_type_fields()
    {
        var assembler = new OpenApiAssembler(new PluginRegistry([new BlogPlugin()]));

        var doc = assembler.Build("Acme", [
            Instance("blog", "devblog", "Dev Blog", "Engineering deep dives."),
        ]);

        var schemas = doc["components"]!["schemas"]!.AsObject();
        schemas.Should().ContainKey("devblog_post");
        schemas.Should().ContainKey("devblog_post_list");

        // Blog's "post" fields become typed properties on data, not a bare object.
        var data = schemas["devblog_post"]!["properties"]!["data"]!.AsObject();
        var props = data["properties"]!.AsObject();
        props.Should().ContainKeys("title", "excerpt", "body", "coverImage", "tags");
        props["title"]!["type"]!.GetValue<string>().Should().Be("string");
        props["body"]!["x-dcms-format"]!.GetValue<string>().Should().Be("richtext");
        props["coverImage"]!["format"]!.GetValue<string>().Should().Be("uuid");
        props["coverImage"]!["x-dcms-media-category"]!.GetValue<string>().Should().Be("image");
        props["tags"]!["type"]!.GetValue<string>().Should().Be("array");

        // Required fields are declared (title and body on a blog post).
        var required = data["required"]!.AsArray().Select(r => r!.GetValue<string>()).ToList();
        required.Should().Contain(["title", "body"]);
    }

    [Fact]
    public void List_returns_the_paged_envelope_and_get_returns_the_item()
    {
        var assembler = new OpenApiAssembler(new PluginRegistry([new BlogPlugin()]));

        var doc = assembler.Build("Acme", [
            Instance("blog", "devblog", "Dev Blog", "Engineering deep dives."),
        ]);

        string ResponseRef(string path) => doc["paths"]![path]!["get"]!["responses"]!["200"]!
            ["content"]!["application/json"]!["schema"]!["$ref"]!.GetValue<string>();

        ResponseRef("/api/devblog/post").Should().Be("#/components/schemas/devblog_post_list");
        ResponseRef("/api/devblog/post/{slug}").Should().Be("#/components/schemas/devblog_post");

        // The envelope wraps an array of the item schema.
        var envelope = doc["components"]!["schemas"]!["devblog_post_list"]!["properties"]!.AsObject();
        envelope.Should().ContainKeys("items", "page", "pageSize", "totalCount");
        envelope["items"]!["items"]!["$ref"]!.GetValue<string>()
            .Should().Be("#/components/schemas/devblog_post");
    }

    [Fact]
    public void Declares_paging_query_params_on_list_and_a_slug_path_param_on_get()
    {
        var assembler = new OpenApiAssembler(new PluginRegistry([new BlogPlugin()]));

        var doc = assembler.Build("Acme", [
            Instance("blog", "devblog", "Dev Blog", "Engineering deep dives."),
        ]);

        var listParams = doc["paths"]!["/api/devblog/post"]!["get"]!["parameters"]!.AsArray()
            .Select(p => p!["name"]!.GetValue<string>()).ToList();
        listParams.Should().Contain(["page", "pageSize"]);

        var getParam = doc["paths"]!["/api/devblog/post/{slug}"]!["get"]!["parameters"]!.AsArray().Single()!;
        getParam["name"]!.GetValue<string>().Should().Be("slug");
        getParam["in"]!.GetValue<string>().Should().Be("path");
        getParam["required"]!.GetValue<bool>().Should().BeTrue();
    }

    [Fact]
    public void Documents_analytics_collect_as_a_post_with_body_and_tenant_header()
    {
        var registry = new PluginRegistry([new AnalyticsPlugin()]);
        var assembler = new OpenApiAssembler(registry);

        var doc = assembler.Build("Acme", [
            Instance("analytics", "stats", "Site Analytics", "Traffic for the marketing site."),
        ]);

        // The ingest beacon is mounted under /api/{slug}/collect as a POST.
        var collect = doc["paths"]!["/api/stats/collect"]!["post"]!.AsObject();
        collect.Should().ContainKey("requestBody");
        collect["requestBody"]!["content"]!["application/json"]!["schema"]!["properties"]!
            .AsObject().Should().ContainKey("type");

        // External callers must identify their tenant via the X-Dcms-Tenant header.
        var header = collect["parameters"]!.AsArray()
            .Single(p => p!["in"]!.GetValue<string>() == "header")!;
        header["name"]!.GetValue<string>().Should().Be("X-Dcms-Tenant");
        header["required"]!.GetValue<bool>().Should().BeTrue();

        // Fire-and-forget: success is an empty 202.
        var responses = collect["responses"]!.AsObject();
        responses.Should().ContainKey("202");
        responses["202"]!.AsObject().Should().NotContainKey("content");
    }

    [Fact]
    public void Disabled_instances_simply_are_not_passed_in()
    {
        var registry = new PluginRegistry([new BlogPlugin()]);
        var assembler = new OpenApiAssembler(registry);

        var doc = assembler.Build("Acme", []);

        doc["paths"]!.AsObject().Should().BeEmpty();
        doc["tags"]!.AsArray().Should().BeEmpty();
    }

    [Fact]
    public void Defaults_to_relative_server_when_no_urls_given()
    {
        var assembler = new OpenApiAssembler(new PluginRegistry([new BlogPlugin()]));

        var servers = assembler.Build("Acme", []).AsObject()["servers"]!.AsArray();

        servers.Should().HaveCount(1);
        servers[0]!["url"]!.GetValue<string>().Should().Be("/");
    }

    [Fact]
    public void Emits_supplied_server_urls_for_the_try_it_pipeline()
    {
        var assembler = new OpenApiAssembler(new PluginRegistry([new BlogPlugin()]));

        var doc = assembler.Build("Acme", [], ["https://acme.example", "https://www.acme.example"]);

        var urls = doc["servers"]!.AsArray().Select(s => s!["url"]!.GetValue<string>()).ToList();
        urls.Should().Equal("https://acme.example", "https://www.acme.example");
    }
}
