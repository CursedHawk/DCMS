using Dcms.PluginSdk.Abstractions.Contracts;
using Dcms.PluginSdk.Runtime;
using Dcms.PluginSdk.Runtime.Contracts;
using Microsoft.Extensions.DependencyInjection;

namespace Dcms.PluginSdk.Tests.Contracts;

public class ContractResolutionTests
{
    private static readonly Guid Tenant = Guid.NewGuid();

    private static async Task<Dcms.PluginSdk.Abstractions.Contracts.IPluginContext> ContextFor(
        string pluginId, PluginRegistry registry, params Dcms.PluginSdk.Abstractions.PluginInstanceContext[] enabled)
    {
        var services = new ServiceCollection().BuildServiceProvider();
        var factory = new PluginContextFactory(registry, new FakeInstanceStore(enabled), services);
        var own = enabled.FirstOrDefault(i => i.PluginId == pluginId);
        return await factory.CreateAsync(Tenant, pluginId, own, PluginActor.Anonymous, CancellationToken.None);
    }

    private static PluginRegistry GreeterAndConsumer(string? bindingKey = null, bool multi = false) => new(
    [
        new TestPlugin("greeter", provides: [ContractProvision.Of<IGreeter, Greeter>()], multi: multi),
        new TestPlugin("forms", consumes: [ContractRequirement.Of<IGreeter>(optional: true, bindingConfigKey: bindingKey)]),
        new TestPlugin("stranger"),
    ]);

    [Fact]
    public async Task Undeclared_contract_is_refused_even_in_process()
    {
        var registry = GreeterAndConsumer();
        var ctx = await ContextFor("stranger", registry, Instances.Of(Tenant, "greeter", "hi"));

        var act = () => ctx.Contracts.Get<IGreeter>();

        act.Should().Throw<InvalidOperationException>().WithMessage("*stranger*does not declare consuming test.greeter@1*");
    }

    [Fact]
    public async Task Resolves_the_sole_enabled_provider_instance()
    {
        var registry = GreeterAndConsumer();
        var ctx = await ContextFor("forms", registry, Instances.Of(Tenant, "greeter", "hi"), Instances.Of(Tenant, "forms", "contact"));

        var result = await ctx.Contracts.Get<IGreeter>().GreetAsync(new GreetInput("Ada"), CancellationToken.None);

        result.Should().Be(new GreetResult("Hello, Ada", "hi"));
    }

    [Fact]
    public async Task No_enabled_provider_is_null_for_TryGet_and_an_error_for_Get()
    {
        var registry = GreeterAndConsumer();
        var ctx = await ContextFor("forms", registry, Instances.Of(Tenant, "forms", "contact"));

        ctx.Contracts.TryGet<IGreeter>().Should().BeNull();
        var act = () => ctx.Contracts.Get<IGreeter>();
        act.Should().Throw<InvalidOperationException>().WithMessage("*No enabled instance of 'greeter'*");
    }

    [Fact]
    public async Task Several_providers_need_a_binding()
    {
        var registry = GreeterAndConsumer(multi: true);
        var ctx = await ContextFor("forms", registry,
            Instances.Of(Tenant, "greeter", "one"), Instances.Of(Tenant, "greeter", "two"), Instances.Of(Tenant, "forms", "contact"));

        var act = () => ctx.Contracts.Get<IGreeter>();

        act.Should().Throw<InvalidOperationException>().WithMessage("*Several instances*must bind one*");
    }

    [Fact]
    public async Task Binding_config_key_picks_the_provider_instance()
    {
        var registry = GreeterAndConsumer(bindingKey: "greeterBinding", multi: true);
        var one = Instances.Of(Tenant, "greeter", "one");
        var two = Instances.Of(Tenant, "greeter", "two");
        var forms = Instances.Of(Tenant, "forms", "contact", new { greeterBinding = two.InstanceId.ToString() });
        var ctx = await ContextFor("forms", registry, one, two, forms);

        var result = await ctx.Contracts.Get<IGreeter>().GreetAsync(new GreetInput("Ada"), CancellationToken.None);

        result.ServedBy.Should().Be("two");
    }

    [Fact]
    public async Task Platform_contract_acts_for_the_caller()
    {
        var registry = new PluginRegistry(
            [new TestPlugin("forms", consumes: [ContractRequirement.Of<ITestClock>()])],
            [ContractProvision.Of<ITestClock, TestClock>()]);
        var ctx = await ContextFor("forms", registry);

        var reading = await ctx.Contracts.Get<ITestClock>().NowAsync(CancellationToken.None);

        reading.CallerPlugin.Should().Be("forms");
    }

    [Fact]
    public async Task Provider_exceptions_surface_unwrapped()
    {
        var registry = new PluginRegistry(
        [
            new TestPlugin("thrower", provides: [ContractProvision.Of<IEcho, ThrowingEcho>()]),
            new TestPlugin("caller", consumes: [ContractRequirement.Of<IEcho>()]),
        ]);
        var ctx = await ContextFor("caller", registry, Instances.Of(Tenant, "thrower", "t"));

        var act = () => ctx.Contracts.Get<IEcho>().EchoAsync(new GreetInput("x"), CancellationToken.None);

        await act.Should().ThrowAsync<ArgumentException>().WithMessage("nope*");
    }

    private sealed class ThrowingEcho : IEcho
    {
        public Task<GreetResult> EchoAsync(GreetInput input, CancellationToken ct) => throw new ArgumentException("nope");
    }
}
