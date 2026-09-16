extern alias AdminApiApp;
using AdminApiApp::Dcms.AdminApi.ApiClientGen;
using Dcms.Shared.Data.Cms;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.Routing.Patterns;
using Microsoft.Extensions.DependencyInjection;

namespace Dcms.IntegrationTests.Plugins;

/// <summary>
/// Keeps <see cref="PluginInstanceSlugs.Reserved"/> honest against the two namespaces it guards:
/// content-api's own routes and the generated client's built-in members. A hand-kept list is only
/// worth having if adding a route without extending it fails the build.
///
/// <para>Container-free, like the audit coverage tests: it reads the endpoint table, not a
/// database.</para>
/// </summary>
public sealed class ReservedInstanceSlugTests : IDisposable
{
    private readonly WebApplicationFactory<Program> _content =
        new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("ConnectionStrings:Postgres", "Host=localhost;Database=unused;Username=unused;Password=unused");
            builder.UseSetting("ConnectionStrings:Redis", "localhost:1");
            builder.UseSetting("Nats:Url", "nats://localhost:1");
        });

    public void Dispose()
    {
        _content.Dispose();
        GC.SuppressFinalize(this);
    }

    [Fact]
    public void Every_literal_content_api_route_segment_is_reserved()
    {
        // The segment after /api/ is where an instance slug goes. Any literal there is a name an
        // instance must not take, because routing prefers the literal.
        var literals = _content.Services.GetRequiredService<EndpointDataSource>().Endpoints
            .OfType<RouteEndpoint>()
            .Select(e => e.RoutePattern.PathSegments)
            .Where(s => s.Count >= 2 && s[0].Parts is [RoutePatternLiteralPart { Content: "api" }])
            .Select(s => s[1].Parts is [RoutePatternLiteralPart literal] ? literal.Content : null)
            .OfType<string>()
            // openapi.json and openapi.yaml cannot be slugs (a dot is not kebab-case); `openapi` can.
            .Where(PluginInstanceSlugs.IsWellFormed)
            .Distinct(StringComparer.Ordinal)
            .ToList();

        literals.Should().NotBeEmpty("the route table should have been read");
        literals.Should().OnlyContain(s => PluginInstanceSlugs.Reserved.ContainsKey(s),
            "a literal route under /api/ shadows an instance of that slug; add it to PluginInstanceSlugs.Reserved");
    }

    [Fact]
    public void Every_client_built_in_an_instance_could_name_is_reserved()
    {
        var nameable = TypeScriptClientEmitter.ReservedTopLevel.Where(PluginInstanceSlugs.IsWellFormed).ToList();

        nameable.Should().NotBeEmpty();
        nameable.Should().OnlyContain(s => PluginInstanceSlugs.Reserved.ContainsKey(s),
            "an instance with this slug would get no typed accessor on the generated client");
    }

    [Theory]
    [InlineData("tags")]
    [InlineData("media")]
    [InlineData("content")]
    [InlineData("analytics")]
    public void A_reserved_slug_is_refused_with_what_owns_it(string slug)
        => PluginInstanceSlugs.Problem(slug).Should().Contain("reserved").And.Contain(slug);

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("Blog")]
    [InlineData("-blog")]
    [InlineData("blog-")]
    [InlineData("my_blog")]
    public void A_malformed_slug_is_refused(string? slug)
        => PluginInstanceSlugs.Problem(slug).Should().Be("slug must be kebab-case.");

    [Theory]
    [InlineData("news")]
    [InlineData("media-kit")]
    [InlineData("content2")]
    public void An_ordinary_slug_is_accepted(string slug)
        => PluginInstanceSlugs.Problem(slug).Should().BeNull();
}
