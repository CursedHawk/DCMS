using Dcms.PluginSdk.Abstractions;
using Dcms.PluginSdk.Abstractions.Contracts;
using Dcms.PluginSdk.Runtime;
using Dcms.PluginSdk.Runtime.Contracts;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Dcms.PluginSdk.Tests.Contracts;

public class OpenContractTests
{
    private static readonly PluginRegistry Registry = new(
    [
        new TestPlugin("greeter", provides: [ContractProvision.Of<IGreeter, Greeter>()]),
        new TestPlugin("loud", provides: [ContractProvision.Of<IGreeter, LoudGreeter>()]),
        new TestPlugin("forms", consumes: [ContractRequirement.Of<IGreeter>(bindingConfigKey: "greeter")]),
    ]);

    private static async Task<IPluginContext> FormsContext(Guid tenant, object? config, params PluginInstanceContext[] others)
    {
        var forms = Instances.Of(tenant, "forms", "contact", config);
        var factory = new PluginContextFactory(Registry,
            new FakeInstanceStore([.. others, forms]), new ServiceCollection().BuildServiceProvider());
        return await factory.CreateAsync(tenant, "forms", forms, PluginActor.Anonymous, TestContext.Current.CancellationToken);
    }

    [Fact]
    public void Several_plugins_may_provide_one_contract()
    {
        Registry.FindContract("test.greeter@1")!.ProviderPluginIds.Should().Equal("greeter", "loud");
    }

    [Fact]
    public async Task The_sole_enabled_provider_serves_whichever_plugin_it_is()
    {
        var tenant = Guid.NewGuid();
        var ctx = await FormsContext(tenant, null, Instances.Of(tenant, "loud", "shout"));

        var result = await ctx.Contracts.Get<IGreeter>().GreetAsync(new GreetInput("ada"), TestContext.Current.CancellationToken);

        result.Should().Be(new GreetResult("HELLO, ADA", "shout"));
    }

    [Fact]
    public async Task A_binding_chooses_across_providers()
    {
        var tenant = Guid.NewGuid();
        var ctx = await FormsContext(tenant, new { greeter = "calm" },
            Instances.Of(tenant, "loud", "shout"), Instances.Of(tenant, "greeter", "calm"));

        var result = await ctx.Contracts.Get<IGreeter>().GreetAsync(new GreetInput("ada"), TestContext.Current.CancellationToken);

        result.ServedBy.Should().Be("calm");
    }

    [Fact]
    public async Task GetAll_returns_every_enabled_provider_instance()
    {
        var tenant = Guid.NewGuid();
        var ctx = await FormsContext(tenant, null,
            Instances.Of(tenant, "loud", "shout"), Instances.Of(tenant, "greeter", "calm"));

        var all = ctx.Contracts.GetAll<IGreeter>();
        var served = new List<string>();
        foreach (var greeter in all)
        {
            served.Add((await greeter.GreetAsync(new GreetInput("x"), TestContext.Current.CancellationToken)).ServedBy);
        }

        served.Should().BeEquivalentTo(["shout", "calm"]);
    }

    [Fact]
    public async Task Ambiguity_without_a_binding_is_an_error_naming_GetAll()
    {
        var tenant = Guid.NewGuid();
        var ctx = await FormsContext(tenant, null,
            Instances.Of(tenant, "loud", "shout"), Instances.Of(tenant, "greeter", "calm"));

        var act = () => ctx.Contracts.Get<IGreeter>();

        act.Should().Throw<InvalidOperationException>().WithMessage("*Several instances*GetAll*");
    }

    [Fact]
    public void A_different_interface_claiming_the_same_id_is_refused()
    {
        var act = () => new PluginRegistry(
        [
            new TestPlugin("greeter", provides: [ContractProvision.Of<IGreeter, Greeter>()]),
            new TestPlugin("impostor", provides: [new ContractProvision(typeof(IImpostorGreeter), typeof(ImpostorGreeter))]),
        ]);

        act.Should().Throw<InvalidOperationException>().WithMessage("*test.greeter@1*two different interfaces*");
    }

    [Fact]
    public void A_plugin_cannot_provide_a_platform_contract()
    {
        var act = () => new PluginRegistry(
            [new TestPlugin("sneaky", provides: [ContractProvision.Of<ITestClock, TestClock>()])]);

        act.Should().Throw<InvalidOperationException>().WithMessage("*dcms.test-clock@1*reserved for the platform*");
    }

    [Fact]
    public void A_required_contract_is_missing_only_when_no_provider_is_enabled()
    {
        var registry = new PluginRegistry(
        [
            new TestPlugin("greeter", provides: [ContractProvision.Of<IGreeter, Greeter>()]),
            new TestPlugin("loud", provides: [ContractProvision.Of<IGreeter, LoudGreeter>()]),
            new TestPlugin("needs", consumes: [ContractRequirement.Of<IGreeter>()]),
        ]);

        PluginDependencies.Missing(registry, "needs", ["loud"]).Should().BeEmpty();
        PluginDependencies.Missing(registry, "needs", []).Should()
            .ContainSingle().Which.ProviderPluginIds.Should().Equal("greeter", "loud");
        PluginDependencies.Dependents(registry, "greeter", [("needs", "n"), ("loud", "l")])
            .Should().BeEmpty("the other provider still serves it");
    }

    [Fact]
    public void The_operator_can_switch_a_plugin_off()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Plugins:Disabled:0"] = "loud" })
            .Build();
        var services = new ServiceCollection();

        services.AddDcmsPlugins(new PluginHost(PluginPlane.Site, config), plugins => plugins
            .Add(new TestPlugin("greeter", provides: [ContractProvision.Of<IGreeter, Greeter>()]))
            .Add(new TestPlugin("loud", provides: [ContractProvision.Of<IGreeter, LoudGreeter>()])));

        var registry = services.BuildServiceProvider().GetRequiredService<PluginRegistry>();
        registry.Find("loud").Should().BeNull();
        registry.FindContract("test.greeter@1")!.ProviderPluginIds.Should().Equal("greeter");
    }

    private sealed class HubPlugin : IPlugin
    {
        public PluginManifest Manifest { get; } = PluginManifest.Create("hub", "Hub", "Maps a host route.", false);

        public PluginPlane? ConfiguredFor { get; private set; }

        public void ConfigureServices(IServiceCollection services, PluginHost host) => ConfiguredFor = host.Plane;

        public void MapHostEndpoints(IEndpointRouteBuilder app, PluginHost host)
        {
            if (host.IsSite)
            {
                app.MapGet("/hub/test", () => "hub");
            }
        }
    }

    [Fact]
    public void Plugins_learn_their_host_and_may_map_routes_outside_an_instance()
    {
        var plugin = new HubPlugin();
        var builder = WebApplication.CreateBuilder();
        builder.Services.AddDcmsPlugins(PluginHost.Empty(PluginPlane.Site), plugins => plugins.Add(plugin));
        var app = builder.Build();

        app.MapDcmsPluginHostEndpoints();

        plugin.ConfiguredFor.Should().Be(PluginPlane.Site);
        ((IEndpointRouteBuilder)app).DataSources.SelectMany(d => d.Endpoints).OfType<RouteEndpoint>()
            .Should().Contain(e => e.RoutePattern.RawText == "/hub/test");
    }
}
