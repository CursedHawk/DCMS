using System.Text.Json;
using Dcms.Plugins.All;
using Dcms.PluginSdk.Runtime;
using Dcms.PluginSdk.Runtime.Contracts;
using Microsoft.Extensions.DependencyInjection;

namespace Dcms.PluginSdk.Tests.Contracts;

public class PluginReferenceTests
{
    private static readonly PluginRegistry Registry = new ServiceCollection()
        .AddDcmsPlugins(p => p.AddAll()).BuildServiceProvider().GetRequiredService<PluginRegistry>();

    [Fact]
    public void Describes_a_content_plugin_for_a_developer()
    {
        var blog = PluginReference.Build(Registry, "blog")!;

        blog.Packages.Select(p => p.Id).Should().Equal("Dcms.PluginSdk.Abstractions", "Dcms.Plugins.Blog.Api");
        var posts = blog.Provides.Should().ContainSingle().Which;
        posts.Id.Should().Be("blog.posts@1");
        posts.ClrType.Should().Be("Dcms.Plugins.Blog.Api.IBlogPosts");
        posts.Operations.Select(o => (o.Method, o.InputType, o.OutputType)).Should().Contain(
            ("ListAsync", "ContentPageRequest", "Task<ContentPage<BlogPost>>"));
        posts.Events.Select(e => e.Name).Should().BeEquivalentTo(["blog.post.published", "blog.post.unpublished"]);
        blog.ContentTypes.Single().PublishedEvent.Should().Be("blog.post.published");
        blog.CSharp.Should().Contain("dotnet add package Dcms.Plugins.Blog.Api")
            .And.Contain("ContractRequirement.Of<IBlogPosts>()")
            .And.Contain("await blogPosts.ListAsync(new ContentPageRequest(/* … */), ct);");
    }

    [Fact]
    public void Shows_how_to_intercept_a_hook()
    {
        var forms = PluginReference.Build(Registry, "forms")!;

        forms.Provides.Single().Hooks.Should().ContainSingle().Which.Name.Should().Be("forms.submitting");
        forms.CSharp.Should().Contain("HookSubscription.Of<FormSubmitting, MyInterceptor>");
        forms.Consumes.Should().Contain(r => r.ContractId == "visitors.identity@1" && r.Optional
                                            // VisitorAuth's visitors, and User Authentication's users as visitors (ADR 0022).
                                            && r.Providers.Order().SequenceEqual(new[] { "user-auth", "visitor-auth" }));
    }

    [Fact]
    public void Serialises_as_plain_json()
    {
        var json = JsonSerializer.Serialize(PluginReference.Build(Registry, "live-chat"), ContractDescriptorBuilder.Json);

        json.Should().Contain("\"risk\":\"read\"").And.Contain("\"live-chat.conversation.started\"");
    }

    [Fact]
    public void An_unknown_plugin_has_no_reference() => PluginReference.Build(Registry, "nope").Should().BeNull();
}
