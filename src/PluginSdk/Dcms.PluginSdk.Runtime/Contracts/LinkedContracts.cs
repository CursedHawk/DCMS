using System.Collections.Concurrent;
using System.Reflection;
using System.Runtime.ExceptionServices;
using Dcms.PluginSdk.Abstractions.Contracts;
using Dcms.Shared.Audit;
using Dcms.Shared.Data;
using Dcms.Shared.Data.Rls;
using Dcms.Shared.Data.Tenancy;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Dcms.PluginSdk.Runtime.Contracts;

/// <summary>A <c>contracts</c> or <c>tables</c> link from one environment to an instance in another (ADR 0028).</summary>
public sealed record ContractLink(Guid Id, string Kind, Guid TargetEnvironmentId, string TargetName, IReadOnlyCollection<string> Grants);

/// <summary>Finds the link a plugin calls through. Scoped; one indexed read per resolution.</summary>
public sealed class ContractLinks(TenancyDbContext db, EnvironmentStore environments)
{
    public async Task<ContractLink?> FindAsync(Guid tenantId, Guid from, string environment, string providerSlug, CancellationToken ct)
    {
        if (await environments.FindAsync(tenantId, environment, ct) is not { } target || target.Id == from) return null;
        using var rls = RlsScope.Environment(tenantId, from);
        var link = await db.EnvironmentLinks.IgnoreTenantFilter().AsNoTracking()
            .Where(l => l.TenantId == tenantId && l.EnvironmentId == from && l.TargetEnvironmentId == target.Id
                        && l.TargetKey == providerSlug
                        && (l.Kind == EnvironmentLinkKinds.Contracts || l.Kind == EnvironmentLinkKinds.Tables))
            .FirstOrDefaultAsync(ct);
        return link is null ? null : new ContractLink(link.Id, link.Kind, target.Id, target.Name, link.Grants);
    }
}

/// <summary>Whether this flow of work is already inside a linked call: links never chain.</summary>
internal static class LinkedCall
{
    private static readonly AsyncLocal<bool> Inside = new();

    public static bool Active => Inside.Value;

    public static IDisposable Enter()
    {
        var previous = Inside.Value;
        Inside.Value = true;
        return new Restore(previous);
    }

    private sealed class Restore(bool previous) : IDisposable
    {
        public void Dispose() => Inside.Value = previous;
    }
}

/// <summary>
/// A linked provider as the calling plugin holds it: every operation checked against the link's
/// grants, counted against its rate, audited with both environments named, and run inside the
/// providing environment's scope -- its database, its caches, its tenant context -- with no way
/// to call through a further link.
/// </summary>
public class LinkedContractProxy : DispatchProxy
{
    // ponytail: per process and per link; a shared counter (Redis) if replicas ever split one caller's traffic.
    private const int CallsPerMinute = 600;
    private static readonly ConcurrentDictionary<Guid, (long Minute, int Count)> Rates = new();

    private object _inner = null!;
    private ContractDescriptor _descriptor = null!;
    private ContractLink _link = null!;
    private Guid _tenantId;
    private Guid _from;
    private string _caller = null!;
    private IServiceProvider _services = null!;

    public static T Create<T>(T inner, ContractDescriptor descriptor, ContractLink link, Guid tenantId, Guid from, string caller, IServiceProvider services)
        where T : class
    {
        var proxy = DispatchProxy.Create<T, LinkedContractProxy>();
        var p = (LinkedContractProxy)(object)proxy;
        (p._inner, p._descriptor, p._link, p._tenantId, p._from, p._caller, p._services) = (inner, descriptor, link, tenantId, from, caller, services);
        return proxy;
    }

    protected override object? Invoke(MethodInfo? method, object?[]? args)
    {
        ArgumentNullException.ThrowIfNull(method);
        var op = _descriptor.Operations.FirstOrDefault(o => o.Method == method)
                 ?? throw new InvalidOperationException($"{method.Name} is not an operation of {_descriptor.Id}.");
        if (LinkedCall.Active)
        {
            throw new ContractAccessDeniedException("A call through an environment link cannot call through another.");
        }
        if (Grant(op.Name, args) is not { } grant || !_link.Grants.Contains(grant))
        {
            throw new ContractAccessDeniedException(
                $"The link to environment '{_link.TargetName}' does not grant {Grant(op.Name, args) ?? op.Name}.");
        }
        var minute = DateTimeOffset.UtcNow.ToUnixTimeSeconds() / 60;
        var rate = Rates.AddOrUpdate(_link.Id, (minute, 1), (_, r) => r.Minute == minute ? (minute, r.Count + 1) : (minute, 1));
        if (rate.Count > CallsPerMinute)
        {
            throw new ContractLimitException($"The link to environment '{_link.TargetName}' allows {CallsPerMinute} calls a minute.");
        }
        if (op.Risk != OpRisk.Read)
        {
            _services.GetService<IAuditRecorder>()?
                .Record(AuditActions.EnvironmentLinkCalled)
                .InTenant(_tenantId)
                .For("environment_link", _link.Id, grant)
                .With("caller", _caller)
                .With("environment", _from)
                .With("target_environment", _link.TargetEnvironmentId)
                .With("operation", grant);
        }

        // Inside the target's scope for the call and everything the provider's async work does
        // after it: the scope is captured by the task it starts.
        using (RlsScope.Environment(_tenantId, _link.TargetEnvironmentId))
        using (LinkedCall.Enter())
        {
            try
            {
                return method.Invoke(_inner, args);
            }
            catch (TargetInvocationException e) when (e.InnerException is not null)
            {
                ExceptionDispatchInfo.Capture(e.InnerException).Throw();
                throw;
            }
        }
    }

    /// <summary>
    /// What the call needs granted: <c>{contract}/{operation}</c> through a contracts link, or
    /// <c>{table}:{verb}</c> through a tables link; null when a tables call names no table.
    /// </summary>
    private string? Grant(string operation, object?[]? args)
    {
        if (_link.Kind != EnvironmentLinkKinds.Tables) return $"{_descriptor.Name}/{operation}";
        var table = args?.FirstOrDefault()?.GetType().GetProperty("Table")?.GetValue(args[0]) as string;
        var verb = operation switch
        {
            "QueryRecords" or "GetRecord" => "read",
            "CreateRecord" or "ImportRecords" or "ValidateImport" => "create",
            "DeleteRecord" or "BulkDeleteRecords" => "delete",
            _ => "update",
        };
        return table is null ? null : $"{table}:{verb}";
    }
}
