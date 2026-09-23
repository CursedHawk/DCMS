using System.Data.Common;
using Dcms.Shared.Kernel.Abstractions;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging;

namespace Dcms.Shared.Data.Rls;

/// <summary>
/// Tells Postgres which tenant a unit of work belongs to, so the row-security policies can
/// enforce what the EF query filters only request (ADR 0015).
///
/// <para>Sets two GUCs: <c>app.tenant_id</c> from an enclosing <see cref="RlsScope.Tenant"/>
/// block, else from <see cref="ITenantContext"/>, which the <c>tenant_isolation</c> policy reads;
/// and <c>app.scope</c> from <see cref="RlsScope.Platform"/>, which <c>platform_scope</c> reads. Both, always, including when a value is empty — an
/// assignment that only ever sets the tenant would leave the previous one in place the first
/// time the tenant is absent.</para>
///
/// <para><b>When.</b> On every connection open, and again before any command whose desired
/// values differ from what this connection was last given. Open alone is not enough: Finbuckle
/// resolves the tenant through the same scoped context it later filters, and a platform scope
/// can be entered while a transaction holds the connection open. The re-check costs a string
/// comparison per command; the round trip happens only when something actually changed.</para>
///
/// <para><b>Why session-level set_config and not SET LOCAL.</b> The values have to survive a
/// commit on a connection EF keeps open, and <c>SET LOCAL</c> would silently drop them there
/// while the bookkeeping here believed they were set. What stops a session value leaking into
/// the next request on the same pooled connection is that nothing relies on it being unset:
/// every open writes both values before anything else runs, and Npgsql resets session state
/// when a connection returns to its pool. <c>TenantGucInterceptorTests</c> checks both.</para>
///
/// <para>Registered only when <c>Rls:Enforce</c> is on (see
/// <see cref="RlsEnforcementServiceCollectionExtensions"/>). While the services still connect
/// as the table owner, the GUC would be set and ignored — a control that looks present and is
/// not — so it is not in the container at all.</para>
/// </summary>
public sealed class TenantGucInterceptor(ITenantContext tenant, ILogger<TenantGucInterceptor> logger)
    : DbCommandInterceptor, IDbConnectionInterceptor
{
    private const string Sql =
        "SELECT set_config('app.tenant_id', @dcms_tenant, false), set_config('app.scope', @dcms_scope, false)";

    /// <summary>What each connection was last given. One instance per request scope, and a
    /// scope can hold several contexts, so it is keyed rather than a single field.</summary>
    private readonly Dictionary<DbConnection, (string Tenant, string Scope)> applied = new();

    private (string Tenant, string Scope) Desired
        => ((RlsScope.TenantOverride ?? tenant.TenantId)?.ToString() ?? string.Empty,
            RlsScope.IsPlatform ? "platform" : string.Empty);

    public void ConnectionOpened(DbConnection connection, ConnectionEndEventData eventData)
        => Apply(connection, transaction: null);

    public Task ConnectionOpenedAsync(
        DbConnection connection, ConnectionEndEventData eventData, CancellationToken cancellationToken = default)
        => ApplyAsync(connection, transaction: null, cancellationToken);

    public override InterceptionResult<DbDataReader> ReaderExecuting(
        DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result)
    {
        Sync(command);
        return result;
    }

    public override InterceptionResult<int> NonQueryExecuting(
        DbCommand command, CommandEventData eventData, InterceptionResult<int> result)
    {
        Sync(command);
        return result;
    }

    public override InterceptionResult<object> ScalarExecuting(
        DbCommand command, CommandEventData eventData, InterceptionResult<object> result)
    {
        Sync(command);
        return result;
    }

    public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
        DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result,
        CancellationToken cancellationToken = default)
    {
        await SyncAsync(command, cancellationToken);
        return result;
    }

    public override async ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
        DbCommand command, CommandEventData eventData, InterceptionResult<int> result,
        CancellationToken cancellationToken = default)
    {
        await SyncAsync(command, cancellationToken);
        return result;
    }

    public override async ValueTask<InterceptionResult<object>> ScalarExecutingAsync(
        DbCommand command, CommandEventData eventData, InterceptionResult<object> result,
        CancellationToken cancellationToken = default)
    {
        await SyncAsync(command, cancellationToken);
        return result;
    }

    private void Sync(DbCommand command)
    {
        if (command.Connection is { } connection && Stale(connection))
        {
            Apply(connection, command.Transaction);
        }
    }

    private Task SyncAsync(DbCommand command, CancellationToken ct)
        => command.Connection is { } connection && Stale(connection)
            ? ApplyAsync(connection, command.Transaction, ct)
            : Task.CompletedTask;

    private bool Stale(DbConnection connection)
        => !applied.TryGetValue(connection, out var last) || last != Desired;

    private void Apply(DbConnection connection, DbTransaction? transaction)
    {
        var desired = Desired;
        using var command = Command(connection, transaction, desired);
        command.ExecuteNonQuery();
        Record(connection, desired);
    }

    private async Task ApplyAsync(DbConnection connection, DbTransaction? transaction, CancellationToken ct)
    {
        var desired = Desired;
        await using var command = Command(connection, transaction, desired);
        await command.ExecuteNonQueryAsync(ct);
        Record(connection, desired);
    }

    private static DbCommand Command(DbConnection connection, DbTransaction? transaction, (string Tenant, string Scope) values)
    {
        var command = connection.CreateCommand();
        command.CommandText = Sql;
        command.Transaction = transaction;
        Add(command, "dcms_tenant", values.Tenant);
        Add(command, "dcms_scope", values.Scope);
        return command;

        static void Add(DbCommand command, string name, string value)
        {
            var parameter = command.CreateParameter();
            parameter.ParameterName = name;
            parameter.Value = value;
            command.Parameters.Add(parameter);
        }
    }

    private void Record(DbConnection connection, (string Tenant, string Scope) values)
    {
        applied[connection] = values;
        // Debug, not information: this runs per unit of work. It is here for the day a page
        // comes back blank under enforcement, when "which tenant did the database think this
        // was" is the first question and nothing else answers it.
        logger.LogDebug("RLS scope set: tenant={Tenant} scope={Scope}", values.Tenant, values.Scope);
    }
}
