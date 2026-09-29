using System.Text.Json;
using System.Xml.Linq;
using Dcms.Plugins.All;
using Dcms.Plugins.Blog.Api;
using Dcms.Plugins.Forms.Api;
using Dcms.PluginSdk.Abstractions;
using Dcms.PluginSdk.Abstractions.Contracts;
using Dcms.PluginSdk.Abstractions.Platform;
using Dcms.PluginSdk.Runtime;
using Dcms.PluginSdk.Runtime.Contracts;
using Microsoft.Extensions.DependencyInjection;

namespace Dcms.PluginSdk.Tests.Contracts;

/// <summary>What another plugin (or an outside developer) builds against: the .Api assemblies.</summary>
public class PluginApiTests
{
    private static readonly string Plugins = Path.Combine(RepoRoot(), "src", "Plugins");

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Dcms.sln")))
        {
            dir = dir.Parent;
        }
        return dir?.FullName ?? throw new InvalidOperationException("Repository root not found.");
    }

    public static TheoryData<string> ApiProjects() =>
        new(Directory.GetDirectories(Plugins, "*.Api").Select(Path.GetFileName).OfType<string>());

    [Theory]
    [MemberData(nameof(ApiProjects))]
    public void An_api_assembly_depends_on_the_sdk_abstractions_only(string project)
    {
        // An .Api is what consumers reference and what ships as a NuGet package: anything it
        // pulls in (the data layer, the plugin itself) becomes every consumer's dependency.
        var csproj = XDocument.Load(Path.Combine(Plugins, project, $"{project}.csproj"));
        var references = csproj.Descendants()
            .Where(e => e.Name.LocalName is "ProjectReference" or "PackageReference" or "FrameworkReference")
            .Select(e => Path.GetFileNameWithoutExtension(((string?)e.Attribute("Include") ?? "").Replace('\\', '/')))
            .ToList();

        references.Should().Equal("Dcms.PluginSdk.Abstractions");
    }

    [Fact]
    public void Forms_builds_against_the_visitor_api_not_the_plugin()
    {
        var csproj = File.ReadAllText(Path.Combine(Plugins, "Dcms.Plugins.Forms", "Dcms.Plugins.Forms.csproj"));

        csproj.Should().Contain("Dcms.Plugins.VisitorAuth.Api.csproj").And.NotContain("Dcms.Plugins.VisitorAuth.csproj");
    }

    [Fact]
    public void The_built_in_set_validates_with_every_content_event_declared()
    {
        var registry = new ServiceCollection().AddDcmsPlugins(p => p.AddAll()).BuildServiceProvider()
            .GetRequiredService<PluginRegistry>();

        registry.FindEvent("blog.post.published")!.Value.Contract.Id.Should().Be("blog.posts@1");
        registry.FindHook("forms.submitting")!.Value.Contract.Id.Should().Be("forms.submissions@1");
        registry.Find("blog")!.ContentTypes.Single().Published.Should().Be<BlogPostPublished>();
    }

    [Fact]
    public void A_content_event_the_plugin_does_not_publish_is_refused()
    {
        var act = () => new PluginRegistry([new StrayEventPlugin()]);

        act.Should().Throw<InvalidOperationException>().WithMessage("*stray*raises 'blog.post.published'*");
    }

    private sealed class StrayEventPlugin : IPlugin
    {
        public PluginManifest Manifest { get; } = PluginManifest.Create("stray", "Stray", "x", false,
            contentTypes: [new ContentTypeDefinition("note", [], false, null, Published: typeof(BlogPostPublished))]);
    }

    private sealed class FakeContent : IPluginContent
    {
        public static readonly JsonElement Post = JsonSerializer.SerializeToElement(new
        {
            title = "Hello",
            body = "<p>Hi</p>",
            coverImage = "", // a cleared reference, as editors leave it
            tags = new[] { "news" },
        });

        public Task<ContentItemDto?> GetBySlugAsync(ContentLookup input, CancellationToken ct) =>
            Task.FromResult<ContentItemDto?>(input.Slug == "hello" ? Item(input.InstanceId!.Value) : null);

        public Task<PagedResult<ContentItemDto>> ListAsync(ContentListRequest input, CancellationToken ct) =>
            Task.FromResult(new PagedResult<ContentItemDto>([Item(input.InstanceId!.Value)], input.Page, input.PageSize, 1));

        public Task<ContentItemDto?> ResolveAsync(ContentRef input, CancellationToken ct) => throw new NotSupportedException();

        private static ContentItemDto Item(Guid instance) =>
            new(Guid.NewGuid(), instance, "post", "hello", 1, Post, DateTimeOffset.UnixEpoch);
    }

    [Fact]
    public async Task A_consumer_reads_another_plugins_content_typed()
    {
        var tenant = Guid.NewGuid();
        var registry = new PluginRegistry(
            [new Plugins.Blog.BlogPlugin(), new TestPlugin("digest", consumes: [ContractRequirement.Of<IBlogPosts>()])],
            [ContractProvision.Of<IPluginContent, FakeContent>()]);
        var blog = Instances.Of(tenant, "blog", "news");
        var factory = new PluginContextFactory(registry, new FakeInstanceStore(blog, Instances.Of(tenant, "digest", "d")),
            new ServiceCollection().BuildServiceProvider());
        var digest = await factory.CreateAsync(tenant, "digest", null, PluginActor.System, TestContext.Current.CancellationToken);

        var posts = digest.Contracts.Get<IBlogPosts>();
        var page = await posts.ListAsync(new ContentPageRequest(), TestContext.Current.CancellationToken);
        var one = await posts.GetAsync(new ContentSlugRequest("hello"), TestContext.Current.CancellationToken);

        page.Items.Should().ContainSingle().Which.Data.Should().BeEquivalentTo(
            new BlogPost("Hello", null, "<p>Hi</p>", null, ["news"]));
        page.Items[0].InstanceId.Should().Be(blog.InstanceId);
        one!.Slug.Should().Be("hello");
        (await posts.GetAsync(new ContentSlugRequest("nope"), TestContext.Current.CancellationToken)).Should().BeNull();
    }

    [Fact]
    public void Forms_declares_its_permission_without_the_platform_assembly()
    {
        FormsPermissions.SubmissionsRead.Should().Be(Dcms.Shared.Security.PlatformPermissions.ContentRead);
    }
}
