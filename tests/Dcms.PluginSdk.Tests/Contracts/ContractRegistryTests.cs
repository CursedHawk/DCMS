using System.Text.Json;
using Dcms.PluginSdk.Abstractions.Contracts;
using Dcms.PluginSdk.Runtime;
using Dcms.PluginSdk.Runtime.Contracts;

namespace Dcms.PluginSdk.Tests.Contracts;

public class ContractRegistryTests
{
    private static readonly ContractProvision GreeterProvision = ContractProvision.Of<IGreeter, Greeter>();

    [Fact]
    public void Descriptor_is_transport_neutral_json()
    {
        var descriptor = ContractDescriptorBuilder.Build(typeof(IGreeter), "greeter");

        var json = JsonSerializer.Serialize(descriptor, ContractDescriptorBuilder.Json);

        json.Should().Be(
            """{"id":"test.greeter@1","name":"test.greeter","major":1,"description":"Greets people.","providerPluginId":"greeter","operations":[{"name":"Greet","risk":0,"permission":null,"expose":5,"returnsExternalText":false,"description":"Greets someone by name.","inputSchema":{"type":"object","properties":{"name":{"type":"string"}},"required":["name"]},"outputSchema":{"type":"object","properties":{"message":{"type":"string"},"servedBy":{"type":"string"}},"required":["message","servedBy"]}},{"name":"SetGreeting","risk":1,"permission":"plugin:greeter:write","expose":2,"returnsExternalText":false,"description":null,"inputSchema":{"type":"object","properties":{"template":{"type":"string"}},"required":["template"]},"outputSchema":null}],"events":[{"name":"test.greeted","schema":{"type":"object","properties":{"name":{"type":"string"}},"required":["name"]}}]}""");
    }

    [Fact]
    public void Plugin_contracts_are_registered_with_their_provider()
    {
        var registry = new PluginRegistry([new TestPlugin("greeter", provides: [GreeterProvision])]);

        var contract = registry.FindContract("test.greeter@1");
        contract.Should().NotBeNull();
        contract!.Descriptor.ProviderPluginId.Should().Be("greeter");
        contract.Implementation.Should().Be<Greeter>();
        registry.FindEvent("test.greeted")!.Value.Contract.Id.Should().Be("test.greeter@1");
    }

    [Fact]
    public void Required_contract_without_a_provider_fails_startup()
    {
        var act = () => new PluginRegistry(
            [new TestPlugin("forms", consumes: [ContractRequirement.Of<IGreeter>()])]);

        act.Should().Throw<InvalidOperationException>().WithMessage("*forms*requires*test.greeter@1*nothing provides*");
    }

    [Fact]
    public void Optional_contract_without_a_provider_is_allowed()
    {
        var act = () => new PluginRegistry(
            [new TestPlugin("forms", consumes: [ContractRequirement.Of<IGreeter>(optional: true)])]);

        act.Should().NotThrow();
    }

    [Fact]
    public void Operation_without_a_stated_risk_fails_startup()
    {
        var act = () => new PluginRegistry(
            [new TestPlugin("bad", provides: [ContractProvision.Of<IUnmarkedOperation, UnmarkedOperation>()])]);

        act.Should().Throw<InvalidOperationException>().WithMessage("*GreetAsync*[Operation(risk)]*");
    }

    [Fact]
    public void Cycle_of_required_contracts_fails_startup()
    {
        var act = () => new PluginRegistry(
        [
            new TestPlugin("a", provides: [GreeterProvision], consumes: [ContractRequirement.Of<IEcho>()]),
            new TestPlugin("b", provides: [ContractProvision.Of<IEcho, Echo>()], consumes: [ContractRequirement.Of<IGreeter>()]),
        ]);

        act.Should().Throw<InvalidOperationException>().WithMessage("*cycle*a -> b -> a*");
    }

    [Fact]
    public void Optional_edge_breaks_a_cycle()
    {
        var act = () => new PluginRegistry(
        [
            new TestPlugin("a", provides: [GreeterProvision], consumes: [ContractRequirement.Of<IEcho>(optional: true)]),
            new TestPlugin("b", provides: [ContractProvision.Of<IEcho, Echo>()], consumes: [ContractRequirement.Of<IGreeter>()]),
        ]);

        act.Should().NotThrow();
    }

    [Fact]
    public void Contract_provided_twice_fails_startup()
    {
        var act = () => new PluginRegistry(
        [
            new TestPlugin("a", provides: [GreeterProvision]),
            new TestPlugin("b", provides: [GreeterProvision]),
        ]);

        act.Should().Throw<InvalidOperationException>().WithMessage("*test.greeter@1*provided twice*");
    }

    [Fact]
    public void Plugin_cannot_provide_a_platform_contract()
    {
        var act = () => new PluginRegistry(
            [new TestPlugin("sneaky", provides: [ContractProvision.Of<ITestClock, TestClock>()])]);

        act.Should().Throw<InvalidOperationException>().WithMessage("*reserved for the platform*");
    }

    [Fact]
    public void Platform_contract_satisfies_a_required_consume()
    {
        var act = () => new PluginRegistry(
            [new TestPlugin("forms", consumes: [ContractRequirement.Of<ITestClock>()])],
            [ContractProvision.Of<ITestClock, TestClock>()]);

        act.Should().NotThrow();
    }

    [Theory]
    [InlineData("visitors.profiles@1", true)]
    [InlineData("dcms.email@2", true)]
    [InlineData("roster.members@0", false)]
    [InlineData("Roster.members@1", false)]
    [InlineData("roster..members@1", false)]
    [InlineData("roster.members", false)]
    public void Contract_ids_are_dotted_kebab_with_a_major(string id, bool valid) =>
        ContractIds.IsValid(id).Should().Be(valid);
}
