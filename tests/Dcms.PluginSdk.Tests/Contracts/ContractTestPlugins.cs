using System.Text.Json;
using Dcms.PluginSdk.Abstractions;
using Dcms.PluginSdk.Abstractions.Contracts;
using Dcms.PluginSdk.Runtime.Contracts;
using Dcms.Shared.Kernel.Abstractions;
using Microsoft.Extensions.DependencyInjection;

namespace Dcms.PluginSdk.Tests.Contracts;

// A small cast of contracts and plugins the contract tests build registries from.

public sealed record GreetInput(string Name);
public sealed record GreetResult(string Message, string ServedBy);
public sealed record SetGreetingInput(string Template);

[ContractEvent("test.greeted")]
public sealed record Greeted(string Name) : IPluginEvent;

[DcmsContract("test.greeter", 1, Description = "Greets people.", Events = [typeof(Greeted)])]
public interface IGreeter
{
    [Operation(OpRisk.Read, Expose = OpExposure.Site | OpExposure.Ai, Description = "Greets someone by name.")]
    Task<GreetResult> GreetAsync(GreetInput input, CancellationToken ct);

    [Operation(OpRisk.Safe, Permission = "plugin:greeter:write", Expose = OpExposure.Admin)]
    Task SetGreetingAsync(SetGreetingInput input, CancellationToken ct);
}

/// <summary>Reports which instance it was constructed for, so tests can see binding decisions.</summary>
public sealed class Greeter(IPluginContext context) : IGreeter
{
    public Task<GreetResult> GreetAsync(GreetInput input, CancellationToken ct) =>
        Task.FromResult(new GreetResult($"Hello, {input.Name}", context.Instance?.Slug ?? "-"));

    public Task SetGreetingAsync(SetGreetingInput input, CancellationToken ct) => Task.CompletedTask;
}

[DcmsContract("test.unmarked", 1)]
public interface IUnmarkedOperation
{
    Task<GreetResult> GreetAsync(GreetInput input, CancellationToken ct);
}

public sealed class UnmarkedOperation : IUnmarkedOperation
{
    public Task<GreetResult> GreetAsync(GreetInput input, CancellationToken ct) => throw new NotSupportedException();
}

[DcmsContract("dcms.test-clock", 1)]
public interface ITestClock
{
    [Operation(OpRisk.Read)]
    Task<ClockReading> NowAsync(CancellationToken ct);
}

public sealed record ClockReading(string CallerPlugin);

public sealed class TestClock(IPluginContext caller) : ITestClock
{
    public Task<ClockReading> NowAsync(CancellationToken ct) => Task.FromResult(new ClockReading(caller.PluginId));
}

[DcmsContract("test.echo", 1)]
public interface IEcho
{
    [Operation(OpRisk.Read)]
    Task<GreetResult> EchoAsync(GreetInput input, CancellationToken ct);
}

public sealed class Echo : IEcho
{
    public Task<GreetResult> EchoAsync(GreetInput input, CancellationToken ct) => Task.FromResult(new GreetResult(input.Name, "echo"));
}

/// <summary>A plugin whose whole manifest is supplied by the test.</summary>
public sealed class TestPlugin(
    string id,
    IReadOnlyList<ContractProvision>? provides = null,
    IReadOnlyList<ContractRequirement>? consumes = null,
    bool multi = false) : IPlugin
{
    public PluginManifest Manifest { get; } = PluginManifest.Create(
        id, id, $"{id} test plugin", allowMultipleInstances: multi, provides: provides, consumes: consumes);
}

/// <summary>A second implementation of <see cref="IGreeter"/>, from another plugin: contracts are open.</summary>
public sealed class LoudGreeter(IPluginContext context) : IGreeter
{
    public Task<GreetResult> GreetAsync(GreetInput input, CancellationToken ct) =>
        Task.FromResult(new GreetResult($"HELLO, {input.Name.ToUpperInvariant()}", context.Instance?.Slug ?? "-"));

    public Task SetGreetingAsync(SetGreetingInput input, CancellationToken ct) => Task.CompletedTask;
}

/// <summary>Claims the id test.greeter@1 with a different interface — must be refused.</summary>
[DcmsContract("test.greeter", 1)]
public interface IImpostorGreeter
{
    [Operation(OpRisk.Read)]
    Task<GreetResult> GreetAsync(GreetInput input, CancellationToken ct);
}

public sealed class ImpostorGreeter : IImpostorGreeter
{
    public Task<GreetResult> GreetAsync(GreetInput input, CancellationToken ct) => throw new NotSupportedException();
}

public sealed class FakeInstanceStore(params PluginInstanceContext[] instances) : IPluginInstanceStore
{
    public Task<IReadOnlyList<PluginInstanceContext>> ListEnabledAsync(Guid tenantId, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<PluginInstanceContext>>(instances.Where(i => i.TenantId == tenantId).ToList());
}

public sealed class FakeTenant(Guid? tenantId) : ITenantContext
{
    public Guid? TenantId => tenantId;
    public string? TenantSlug => "acme";
}

public static class Instances
{
    public static PluginInstanceContext Of(Guid tenantId, string pluginId, string slug, object? config = null) =>
        new(Guid.NewGuid(), tenantId, pluginId, slug, slug, $"{slug} instance",
            JsonDocument.Parse(JsonSerializer.Serialize(config ?? new { })));
}
