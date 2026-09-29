using System.Diagnostics;
using Dcms.PluginSdk.Abstractions;
using Dcms.PluginSdk.Abstractions.Contracts;
using Dcms.Shared.Telemetry;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Dcms.PluginSdk.Runtime.Contracts;

/// <summary>
/// Runs a hook through every plugin intercepting it that has an enabled instance in the tenant,
/// highest priority first. Each handler sees the payload the previous one returned; the first to
/// cancel ends the chain.
///
/// <para>Fail-open by design: a hook asks other plugins' opinion about an operation that belongs
/// to the provider, so a handler that throws or overruns <see cref="DefaultTimeout"/> is logged,
/// traced and skipped rather than allowed to break the provider's request.</para>
/// </summary>
internal sealed class PluginHookRunner(
    Guid tenantId,
    string callerPluginId,
    PluginActor actor,
    IReadOnlyList<PluginInstanceContext> enabled,
    PluginRegistry registry,
    IServiceProvider services) : IPluginHooks
{
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(2);

    public async Task<HookOutcome<THook>> RunAsync<THook>(THook hook, CancellationToken ct) where THook : IPluginHook
    {
        var name = ContractIds.HookName(typeof(THook));
        if (registry.FindHook(name) is not { } found
            || registry.FindContract(found.Contract.Id)?.ProvidedBy(callerPluginId) is null)
        {
            throw new InvalidOperationException(
                $"Plugin '{callerPluginId}' does not provide a contract declaring hook '{name}'.");
        }

        var timeout = services.GetService<PluginHost>()?.Configuration.GetValue("Plugins:HookTimeoutMs", 0) is > 0 and var ms
            ? TimeSpan.FromMilliseconds(ms)
            : DefaultTimeout;
        var logger = services.GetService<ILoggerFactory>()?.CreateLogger<PluginHookRunner>();
        var enabledPlugins = enabled.Select(i => i.PluginId).ToHashSet(StringComparer.Ordinal);

        var value = hook;
        foreach (var (pluginId, subscription) in registry.InterceptorsOf(name))
        {
            if (!enabledPlugins.Contains(pluginId))
            {
                continue;
            }

            using var activity = DcmsActivitySource.Start("dcms.hook");
            activity?.SetTag("dcms.hook", name);
            activity?.SetTag("dcms.plugin.caller", callerPluginId);
            activity?.SetTag("dcms.plugin.interceptor", pluginId);

            // Tenant-wide, like an event handler; the plugin's instance when it has exactly one.
            var own = enabled.Where(i => i.PluginId == pluginId).ToList();
            var context = new PluginContext(
                tenantId, pluginId, own.Count == 1 ? own[0] : null, actor, enabled, registry, services);
            var handler = (IPluginHookHandler<THook>)ContractActivator.Create(services, subscription.Handler, context);

            using var budget = CancellationTokenSource.CreateLinkedTokenSource(ct);
            budget.CancelAfter(timeout);
            HookResult<THook> result;
            try
            {
                result = await handler.HandleAsync(value, context, budget.Token).AsTask().WaitAsync(budget.Token);
            }
            catch (Exception e) when (!ct.IsCancellationRequested)
            {
                activity?.SetStatus(ActivityStatusCode.Error, e.Message);
                logger?.LogWarning(e, "Hook {Hook} handler of plugin {Plugin} failed or timed out; skipped.", name, pluginId);
                continue;
            }

            value = result.Value ?? value;
            if (result.Cancelled)
            {
                activity?.SetTag("dcms.hook.cancelled", true);
                return new HookOutcome<THook>(value, true, result.Reason, pluginId);
            }
        }
        return new HookOutcome<THook>(value, false, null, null);
    }
}
