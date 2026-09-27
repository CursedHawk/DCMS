using System.Diagnostics;
using System.Reflection;
using System.Runtime.ExceptionServices;
using Dcms.PluginSdk.Abstractions.Contracts;
using Dcms.Shared.Audit;
using Dcms.Shared.Telemetry;
using Microsoft.Extensions.DependencyInjection;

namespace Dcms.PluginSdk.Runtime.Contracts;

/// <summary>
/// What a plugin actually holds when it resolves a contract: the provider behind a proxy that
/// traces every call as a <c>dcms.contract</c> span and audits every writing one. The provider
/// cannot be reached around it — providers are not registered in DI, only constructed here.
///
/// <para>Permissions are deliberately <b>not</b> checked here. In-process, the consumer's
/// <c>Consumes</c> declaration is the grant: a Forms submission by an anonymous visitor must be
/// able to queue an email. <see cref="OperationAttribute.Permission"/> gates the external planes
/// (admin, AI, site), which the contract dispatcher enforces.</para>
/// </summary>
public class ContractProxy : DispatchProxy
{
    private object _target = null!;
    private ContractDescriptor _descriptor = null!;
    private string _callerPluginId = null!;
    private IServiceProvider _services = null!;

    public static T Create<T>(T target, ContractDescriptor descriptor, string callerPluginId, IServiceProvider services)
        where T : class
    {
        var proxy = DispatchProxy.Create<T, ContractProxy>();
        var p = (ContractProxy)(object)proxy;
        p._target = target;
        p._descriptor = descriptor;
        p._callerPluginId = callerPluginId;
        p._services = services;
        return proxy;
    }

    protected override object? Invoke(MethodInfo? method, object?[]? args)
    {
        ArgumentNullException.ThrowIfNull(method);
        var op = _descriptor.Operations.FirstOrDefault(o => o.Method == method)
            ?? throw new InvalidOperationException($"{method.Name} is not an operation of {_descriptor.Id}.");

        var parent = Activity.Current;
        var activity = DcmsActivitySource.Start("dcms.contract");
        activity?.SetTag("dcms.contract", _descriptor.Id);
        activity?.SetTag("dcms.contract.operation", op.Name);
        activity?.SetTag("dcms.plugin.caller", _callerPluginId);
        activity?.SetTag("dcms.plugin.provider", _descriptor.ProviderPluginId ?? "platform");

        AuditEntry? audit = null;
        if (op.Risk != OpRisk.Read)
        {
            audit = _services.GetService<IAuditRecorder>()?
                .Record(AuditActions.PluginContractInvoked)
                .For("contract", _descriptor.Id, op.Name)
                .With("operation", op.Name)
                .With("caller", _callerPluginId)
                .With("risk", op.Risk.ToString());
        }

        object? result;
        try
        {
            result = method.Invoke(_target, args);
        }
        catch (TargetInvocationException e) when (e.InnerException is not null)
        {
            Fail(activity, audit, e.InnerException);
            ExceptionDispatchInfo.Capture(e.InnerException).Throw();
            throw;
        }
        finally
        {
            // The span must cover the provider's async work, but must not stay ambient in the
            // caller once this synchronous call returns.
            Activity.Current = parent;
        }

        if (result is Task task)
        {
            task.ContinueWith(
                t =>
                {
                    if (t.IsFaulted)
                    {
                        Fail(activity, audit, t.Exception!.GetBaseException());
                    }
                    activity?.Dispose();
                },
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
        }
        else
        {
            activity?.Dispose();
        }
        return result;
    }

    private static void Fail(Activity? activity, AuditEntry? audit, Exception error)
    {
        activity?.SetStatus(ActivityStatusCode.Error, error.GetType().Name);
        audit?.Failed(error.GetType().Name);
    }
}
