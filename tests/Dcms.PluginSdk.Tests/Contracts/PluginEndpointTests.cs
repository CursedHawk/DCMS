using System.Net;
using Dcms.PluginSdk.Abstractions;
using Dcms.PluginSdk.Abstractions.Contracts;
using Dcms.PluginSdk.Runtime;
using Dcms.PluginSdk.Runtime.Contracts;
using Dcms.Shared.Kernel.Abstractions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace Dcms.PluginSdk.Tests.Contracts;

public class PluginEndpointTests
{
    private static readonly Guid Tenant = Guid.NewGuid();

    public sealed class HelloPlugin : IPlugin
    {
        public PluginManifest Manifest { get; } = PluginManifest.Create("hello", "Hello", "Says hello.", allowMultipleInstances: true);

        public void ConfigureServices(IServiceCollection services)
        {
        }

        public void MapEndpoints(IPluginEndpointBuilder endpoints)
        {
            endpoints.MapContentList("greeting");
            endpoints.MapGet("/hello", (IPluginContext ctx) => Results.Ok($"{ctx.PluginId}:{ctx.Instance!.Slug}"));
        }

        public OpenApiFragment BuildOpenApiFragment(PluginInstanceContext instance) => OpenApiFragment.Empty;
    }

    public sealed class ClashingPlugin : IPlugin
    {
        public PluginManifest Manifest { get; } = PluginManifest.Create("clash", "Clash", "Maps the same route.", allowMultipleInstances: false);

        public void ConfigureServices(IServiceCollection services)
        {
        }

        public void MapEndpoints(IPluginEndpointBuilder endpoints) => endpoints.MapGet("/hello", () => "clash");

        public OpenApiFragment BuildOpenApiFragment(PluginInstanceContext instance) => OpenApiFragment.Empty;
    }

    private static async Task<WebApplication> StartAsync(Action<PluginRegistryBuilder> plugins, params PluginInstanceContext[] enabled)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddRouting();
        builder.Services.AddSingleton<ITenantContext>(new FakeTenant(Tenant));
        builder.Services.AddSingleton<IPluginInstanceStore>(new FakeInstanceStore(enabled));
        builder.Services.AddDcmsPlugins(plugins);
        var app = builder.Build();
        app.MapDcmsPlugins();
        await app.StartAsync(TestContext.Current.CancellationToken);
        return app;
    }

    [Fact]
    public async Task Plugin_route_resolves_only_for_an_enabled_instance_of_that_plugin()
    {
        await using var app = await StartAsync(
            p => p.Add<HelloPlugin>(),
            Instances.Of(Tenant, "hello", "greetings"),
            Instances.Of(Tenant, "blog", "news"));
        var client = app.GetTestClient();

        var ok = await client.GetAsync("/api/greetings/hello", TestContext.Current.CancellationToken);
        ok.StatusCode.Should().Be(HttpStatusCode.OK);
        (await ok.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)).Should().Be("\"hello:greetings\"");

        (await client.GetAsync("/api/news/hello", TestContext.Current.CancellationToken)).StatusCode.Should().Be(HttpStatusCode.NotFound, "another plugin's slug");
        (await client.GetAsync("/api/missing/hello", TestContext.Current.CancellationToken)).StatusCode.Should().Be(HttpStatusCode.NotFound, "disabled or absent instance");
    }

    [Fact]
    public async Task Two_plugins_mapping_the_same_route_fail_startup()
    {
        var act = () => StartAsync(p => p.Add<HelloPlugin>().Add<ClashingPlugin>());

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*'hello' and 'clash' both map GET /hello*");
    }
}
