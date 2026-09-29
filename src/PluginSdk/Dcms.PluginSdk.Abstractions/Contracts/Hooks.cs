namespace Dcms.PluginSdk.Abstractions.Contracts;

/// <summary>Marker for hook records a contract lets other plugins intercept (see <see cref="ContractHookAttribute"/>).</summary>
public interface IPluginHook;

/// <summary>
/// Names a hook, e.g. <c>forms.submitting</c>. Declared on a contract with
/// <c>[DcmsContract(..., Hooks = [typeof(FormSubmitting)])]</c>.
///
/// <para>An event says something happened, and is delivered later, off the request. A hook asks
/// "may this happen, and in what form?" <i>before</i> it does: the provider runs it in-process,
/// every intercepting plugin enabled in the tenant sees it in priority order, and each may
/// replace the payload or cancel. A spam filter vetoing a form submission, a CRM enriching a
/// registration, a policy plugin refusing a publish.</para>
/// </summary>
[AttributeUsage(AttributeTargets.Class, Inherited = false)]
public sealed class ContractHookAttribute(string name) : Attribute
{
    public string Name { get; } = name;
}

/// <summary>What one handler decided.</summary>
public readonly record struct HookResult<THook>(THook Value, bool Cancelled, string? Reason)
    where THook : IPluginHook;

public static class HookResult
{
    /// <summary>Let it proceed, with this (possibly replaced) payload.</summary>
    public static HookResult<THook> Continue<THook>(THook value) where THook : IPluginHook => new(value, false, null);

    /// <summary>Stop it. The reason reaches the provider, which decides what the user sees.</summary>
    public static HookResult<THook> Cancel<THook>(THook value, string reason) where THook : IPluginHook => new(value, true, reason);
}

/// <summary>What the chain decided, for the provider that ran it.</summary>
/// <param name="CancelledBy">The plugin that cancelled; null when it proceeds.</param>
public sealed record HookOutcome<THook>(THook Value, bool Cancelled, string? Reason, string? CancelledBy)
    where THook : IPluginHook;

/// <summary>
/// Intercepts a hook. Runs synchronously on the provider's request, so it must be quick: a
/// handler that throws or overruns its time budget is skipped (logged and traced), never allowed
/// to fail the operation it was only asked about.
/// </summary>
public interface IPluginHookHandler<THook> where THook : IPluginHook
{
    ValueTask<HookResult<THook>> HandleAsync(THook hook, IPluginContext context, CancellationToken ct);
}

/// <summary>An interception a plugin registers: higher <see cref="Priority"/> runs first.</summary>
public sealed record HookSubscription(string HookName, Type HookType, Type Handler, int Priority = 0)
{
    public static HookSubscription Of<THook, THandler>(int priority = 0)
        where THook : IPluginHook
        where THandler : IPluginHookHandler<THook>
        => new(ContractIds.HookName(typeof(THook)), typeof(THook), typeof(THandler), priority);
}

/// <summary>Runs hooks. Only a provider of the contract declaring a hook may run it.</summary>
public interface IPluginHooks
{
    Task<HookOutcome<THook>> RunAsync<THook>(THook hook, CancellationToken ct) where THook : IPluginHook;
}
