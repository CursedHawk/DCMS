using Dcms.PluginSdk.Abstractions.Contracts;
using Dcms.PluginSdk.Runtime;
using Dcms.PluginSdk.Runtime.Contracts;
using Microsoft.Extensions.DependencyInjection;

namespace Dcms.PluginSdk.Tests.Contracts;

public class PluginDependencyTests
{
    private static readonly PluginRegistry Registry = new(
    [
        new TestPlugin("greeter", provides: [ContractProvision.Of<IGreeter, Greeter>()], multi: true),
        new TestPlugin("needs", consumes: [ContractRequirement.Of<IGreeter>()]),
        new TestPlugin("wants", consumes: [ContractRequirement.Of<IGreeter>(optional: true)]),
    ]);

    [Fact]
    public void Enabling_a_consumer_without_its_required_provider_is_refused()
    {
        PluginDependencies.Missing(Registry, "needs", ["wants"])
            .Should().BeEquivalentTo([new MissingProvider("test.greeter@1", ["greeter"])]);
        PluginDependencies.Missing(Registry, "needs", ["greeter"]).Should().BeEmpty();
        PluginDependencies.Missing(Registry, "wants", []).Should().BeEmpty("an optional consumer degrades instead");
    }

    [Fact]
    public void Disabling_the_last_provider_names_its_required_dependents_only()
    {
        var after = new List<(string, string)> { ("needs", "n1"), ("wants", "w1") };

        PluginDependencies.Dependents(Registry, "greeter", after)
            .Should().Equal(new DependentInstance("needs", "n1", "test.greeter@1"));

        PluginDependencies.Dependents(Registry, "greeter", [.. after, ("greeter", "g2")])
            .Should().BeEmpty("another greeter instance still provides the contract");
    }

    [Fact]
    public async Task A_binding_may_name_the_provider_by_slug()
    {
        var tenant = Guid.NewGuid();
        var registry = new PluginRegistry(
        [
            new TestPlugin("greeter", provides: [ContractProvision.Of<IGreeter, Greeter>()], multi: true),
            new TestPlugin("forms", consumes: [ContractRequirement.Of<IGreeter>(bindingConfigKey: "greeterSlug")]),
        ]);
        var forms = Instances.Of(tenant, "forms", "contact", new { greeterSlug = "two" });
        var factory = new PluginContextFactory(registry,
            new FakeInstanceStore(Instances.Of(tenant, "greeter", "one"), Instances.Of(tenant, "greeter", "two"), forms),
            new ServiceCollection().BuildServiceProvider());
        var ctx = await factory.CreateAsync(tenant, "forms", forms, PluginActor.Anonymous, TestContext.Current.CancellationToken);

        var result = await ctx.Contracts.Get<IGreeter>().GreetAsync(new GreetInput("x"), TestContext.Current.CancellationToken);

        result.ServedBy.Should().Be("two");
    }
}
