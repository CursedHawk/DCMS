using Dcms.PluginSdk.Abstractions;
using Dcms.PluginSdk.Abstractions.Contracts;
using Dcms.PluginSdk.Runtime;
using Dcms.PluginSdk.Runtime.Contracts;
using Microsoft.Extensions.DependencyInjection;

namespace Dcms.PluginSdk.Tests.Contracts;

[ContractHook("test.submitting")]
public sealed record Submitting(string Message, IReadOnlyList<string> Stamps) : IPluginHook;

[DcmsContract("test.inbox", 1, Hooks = [typeof(Submitting)])]
public interface IInbox
{
    [Operation(OpRisk.Safe)]
    Task SubmitAsync(Submitting input, CancellationToken ct);
}

public sealed class Inbox : IInbox
{
    public Task SubmitAsync(Submitting input, CancellationToken ct) => Task.CompletedTask;
}

public sealed class Stamp(IPluginContext context) : IPluginHookHandler<Submitting>
{
    public ValueTask<HookResult<Submitting>> HandleAsync(Submitting hook, IPluginContext _, CancellationToken ct) =>
        ValueTask.FromResult(HookResult.Continue(hook with { Stamps = [.. hook.Stamps, context.PluginId] }));
}

public sealed class SpamFilter : IPluginHookHandler<Submitting>
{
    public ValueTask<HookResult<Submitting>> HandleAsync(Submitting hook, IPluginContext context, CancellationToken ct) =>
        ValueTask.FromResult(hook.Message.Contains("spam")
            ? HookResult.Cancel(hook, "Looks like spam.")
            : HookResult.Continue(hook));
}

public sealed class Broken : IPluginHookHandler<Submitting>
{
    public ValueTask<HookResult<Submitting>> HandleAsync(Submitting hook, IPluginContext context, CancellationToken ct) =>
        throw new InvalidOperationException("boom");
}

public sealed class Slow : IPluginHookHandler<Submitting>
{
    public async ValueTask<HookResult<Submitting>> HandleAsync(Submitting hook, IPluginContext context, CancellationToken ct)
    {
        await Task.Delay(Timeout.Infinite, ct);
        return HookResult.Continue(hook);
    }
}

public class HookTests
{
    private sealed class Interceptor(string id, HookSubscription subscription) : IPlugin
    {
        public PluginManifest Manifest { get; } = PluginManifest.Create(
            id, id, id, false, consumes: [ContractRequirement.Of<IInbox>()], intercepts: [subscription]);
    }

    private static readonly PluginRegistry Registry = new(
    [
        new TestPlugin("inbox", provides: [ContractProvision.Of<IInbox, Inbox>()]),
        new Interceptor("late-stamp", HookSubscription.Of<Submitting, Stamp>(priority: -10)),
        new Interceptor("early-stamp", HookSubscription.Of<Submitting, Stamp>(priority: 10)),
        new Interceptor("spam", HookSubscription.Of<Submitting, SpamFilter>()),
        new Interceptor("broken", HookSubscription.Of<Submitting, Broken>(priority: 5)),
        new Interceptor("slow", HookSubscription.Of<Submitting, Slow>(priority: 4)),
        new Interceptor("absent", HookSubscription.Of<Submitting, Stamp>(priority: 100)),
    ]);

    private static async Task<IPluginContext> Context(string pluginId)
    {
        var tenant = Guid.NewGuid();
        var enabled = new[] { "inbox", "late-stamp", "early-stamp", "spam", "broken", "slow" }
            .Select(id => Instances.Of(tenant, id, id)).ToArray();
        var factory = new PluginContextFactory(Registry, new FakeInstanceStore(enabled), new ServiceCollection().BuildServiceProvider());
        return await factory.CreateAsync(tenant, pluginId, null, PluginActor.Anonymous, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Interceptors_run_by_priority_and_may_replace_the_payload()
    {
        var inbox = await Context("inbox");

        var outcome = await inbox.Hooks.RunAsync(new Submitting("hello", []), TestContext.Current.CancellationToken);

        outcome.Cancelled.Should().BeFalse();
        outcome.Value.Stamps.Should().Equal(["early-stamp", "late-stamp"],
            "the plugin with no enabled instance never runs, and failures are skipped");
    }

    [Fact]
    public async Task The_first_cancel_ends_the_chain()
    {
        var inbox = await Context("inbox");

        var outcome = await inbox.Hooks.RunAsync(new Submitting("buy spam", []), TestContext.Current.CancellationToken);

        outcome.Should().BeEquivalentTo(new { Cancelled = true, Reason = "Looks like spam.", CancelledBy = "spam" });
        outcome.Value.Stamps.Should().Equal(["early-stamp"], "late-stamp runs after the filter and never sees it");
    }

    [Fact]
    public async Task Only_a_provider_may_run_its_hooks()
    {
        var other = await Context("spam");

        var act = () => other.Hooks.RunAsync(new Submitting("x", []), TestContext.Current.CancellationToken);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*spam*does not provide*test.submitting*");
    }

    [Fact]
    public void Intercepting_needs_the_contract_consumed()
    {
        var act = () => new PluginRegistry(
        [
            new TestPlugin("inbox", provides: [ContractProvision.Of<IInbox, Inbox>()]),
            new TestPlugin("rogue") { },
            new Rogue(),
        ]);

        act.Should().Throw<InvalidOperationException>().WithMessage("*rogue2*intercepts*test.submitting*does not consume*");
    }

    private sealed class Rogue : IPlugin
    {
        public PluginManifest Manifest { get; } = PluginManifest.Create(
            "rogue2", "r", "r", false, intercepts: [HookSubscription.Of<Submitting, SpamFilter>()]);
    }

    [Fact]
    public void Hooks_are_part_of_the_descriptor()
    {
        var descriptor = ContractDescriptorBuilder.Build(typeof(IInbox));

        descriptor.Hooks.Should().ContainSingle().Which.Name.Should().Be("test.submitting");
    }
}
