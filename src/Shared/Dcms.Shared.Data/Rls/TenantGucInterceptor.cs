using System.Data.Common;
using System.Runtime.CompilerServices;
using Dcms.Shared.Kernel.Abstractions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using OpenTelemetry;

namespace Dcms.Shared.Data.Rls;

/// <summary>
/// Tells Postgres which tenant a unit of work belongs to, so the row-security policies can
/// enforce what the EF query filters only request (ADR 0015).
///
/// <para>Sets two GUCs: <c>app.tenant_id</c> from an enclosing <see cref="RlsScope.Tenant"/>
/// block, else from the context's <see cref="ITenantContext"/>, which the <c>tenant_isolation</c>
/// policy reads; and <c>app.scope</c> from <see cref="RlsScope.Platform"/>, which <c>platform_scope</c>
/// reads. Both, always, including when a value is empty — an assignment that only ever sets the
/// tenant would leave the previous one in place the first time the tenant is absent.</para>
///
/// <para><b>How: in front of the command itself.</b> Before any command whose desired values differ
/// from what its connection was last given, <c>SET app.tenant_id = '…'; SET app.scope = '…';</c> is
/// prepended to the command's own text, so the values travel in the same round trip (Npgsql skips the
/// result-less statements when reading). Until 2026-10-23 they were a separate awaited
/// <c>set_config</c> on every connection open — 2.2 per request, 10–14 % of the EF services' CPU. The
/// values are a <see cref="Guid"/> and two constants, never caller text, which is what makes inlining
/// them safe. Two kinds of command still get a separate round trip: migrations, because <c>VACUUM</c>
/// and <c>CREATE INDEX CONCURRENTLY</c> refuse to run inside the transaction block a multi-statement
/// command forms; and <c>SaveChanges</c> batches, because EF checks each statement's row count by its
/// position, and two SETs in front made every UPDATE look like a concurrency conflict.</para>
///
/// <para><b>When the database forgets.</b> A command that fails takes its prepended SETs with it (the
/// statements form one implicit transaction), and a rollback undoes every SET made inside it; both
/// mark the connection as given nothing, so the next command sets the values again. A new physical
/// session starts with nothing too: Npgsql resets session state when a connection returns to its pool,
/// and every open is treated as fresh. <c>TenantGucInterceptorTests</c> checks all of these.</para>
///
/// <para><b>Raw commands.</b> A command made with <c>GetDbConnection().CreateCommand()</c> passes no EF
/// interceptor, so it carries nothing and RLS shows it nothing (fail-closed). Such code calls
/// <see cref="Prepare"/> first; <c>RawCommandRlsCoverageTests</c> fails the build when it does not.</para>
///
/// <para>Registered only when <c>Rls:Enforce</c> is on (see
/// <see cref="RlsEnforcementServiceCollectionExtensions"/>). While the services still connect
/// as the table owner, the GUC would be set and ignored — a control that looks present and is
/// not — so it is not in the container at all. A singleton: contexts are pooled
/// (<see cref="ScopedDbContext"/>), and the tenant is read from the context each command runs for.</para>
/// </summary>
public sealed class TenantGucInterceptor(ILogger<TenantGucInterceptor> logger)
    : DbCommandInterceptor, IDbConnectionInterceptor, IDbTransactionInterceptor
{
    private const string TenantSet = "SET app.tenant_id = '";

    /// <summary>What each open connection was last given. On the connection object, so it lives exactly
    /// as long as the connection.</summary>
    private static readonly ConditionalWeakTable<DbConnection, Applied> State = new();

    /// <summary><see cref="Values"/> null means not known -- after a failure or a rollback the session may
    /// still hold an earlier value -- unless <see cref="Fresh"/>: just opened, which Npgsql's reset has
    /// left holding nothing.</summary>
    private sealed class Applied
    {
        public (string Tenant, string Scope)? Values;
        public bool Fresh;
    }

    private static (string Tenant, string Scope) Desired(DbContext? context)
        => ((RlsScope.TenantOverride ?? TenantOf(context))?.ToString() ?? string.Empty,
            RlsScope.IsPlatform ? "platform" : string.Empty);

    private static Guid? TenantOf(DbContext? context) => context is ScopedDbContext scoped
        ? scoped.TenantContext?.TenantId
        : ScopedDbContext.ServicesOf(context)?.GetService<ITenantContext>()?.TenantId;

    /// <summary>
    /// For a raw command on a connection EF opened: puts the tenant in front of it, as the interceptor
    /// does for EF's own commands. Nothing when RLS is not enforced (the context has no interceptor).
    /// </summary>
    public static void Prepare(DbContext db, DbCommand command)
        => db.GetService<IDbContextOptions>().FindExtension<CoreOptionsExtension>()?.Interceptors?
            .OfType<TenantGucInterceptor>().FirstOrDefault()?.Prefix(command, db);

    /// <summary>The command text as written, without what this interceptor put in front of it.</summary>
    public static string WithoutPrefix(string sql)
    {
        if (!sql.StartsWith(TenantSet, StringComparison.Ordinal))
        {
            return sql;
        }
        const string ScopeSet = "SET app.scope = '";
        var scope = sql.IndexOf(ScopeSet, StringComparison.Ordinal);
        var end = scope < 0 ? -1 : sql.IndexOf("'; ", scope + ScopeSet.Length, StringComparison.Ordinal);
        return end < 0 ? sql : sql[(end + 3)..];
    }

    public void ConnectionOpened(DbConnection connection, ConnectionEndEventData eventData)
        => Opened(connection);

    public Task ConnectionOpenedAsync(
        DbConnection connection, ConnectionEndEventData eventData, CancellationToken cancellationToken = default)
    {
        Opened(connection);
        return Task.CompletedTask;
    }

    private static void Opened(DbConnection connection)
    {
        var state = State.GetOrCreateValue(connection);
        state.Values = null;
        state.Fresh = true;
    }

    public override InterceptionResult<DbDataReader> ReaderExecuting(
        DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result)
    {
        Sync(command, eventData);
        return result;
    }

    public override InterceptionResult<int> NonQueryExecuting(
        DbCommand command, CommandEventData eventData, InterceptionResult<int> result)
    {
        Sync(command, eventData);
        return result;
    }

    public override InterceptionResult<object> ScalarExecuting(
        DbCommand command, CommandEventData eventData, InterceptionResult<object> result)
    {
        Sync(command, eventData);
        return result;
    }

    public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
        DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result,
        CancellationToken cancellationToken = default)
    {
        await SyncAsync(command, eventData, cancellationToken);
        return result;
    }

    public override async ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
        DbCommand command, CommandEventData eventData, InterceptionResult<int> result,
        CancellationToken cancellationToken = default)
    {
        await SyncAsync(command, eventData, cancellationToken);
        return result;
    }

    public override async ValueTask<InterceptionResult<object>> ScalarExecutingAsync(
        DbCommand command, CommandEventData eventData, InterceptionResult<object> result,
        CancellationToken cancellationToken = default)
    {
        await SyncAsync(command, eventData, cancellationToken);
        return result;
    }

    public override void CommandFailed(DbCommand command, CommandErrorEventData eventData) => Forget(command.Connection);

    public override Task CommandFailedAsync(DbCommand command, CommandErrorEventData eventData, CancellationToken cancellationToken = default)
    {
        Forget(command.Connection);
        return Task.CompletedTask;
    }

    public void TransactionRolledBack(DbTransaction transaction, TransactionEndEventData eventData) => Forget(transaction.Connection);

    public Task TransactionRolledBackAsync(DbTransaction transaction, TransactionEndEventData eventData, CancellationToken cancellationToken = default)
    {
        Forget(transaction.Connection);
        return Task.CompletedTask;
    }

    public void RolledBackToSavepoint(DbTransaction transaction, TransactionEventData eventData) => Forget(transaction.Connection);

    public Task RolledBackToSavepointAsync(DbTransaction transaction, TransactionEventData eventData, CancellationToken cancellationToken = default)
    {
        Forget(transaction.Connection);
        return Task.CompletedTask;
    }

    public void TransactionFailed(DbTransaction transaction, TransactionErrorEventData eventData) => Forget(transaction.Connection);

    public Task TransactionFailedAsync(DbTransaction transaction, TransactionErrorEventData eventData, CancellationToken cancellationToken = default)
    {
        Forget(transaction.Connection);
        return Task.CompletedTask;
    }

    private static void Forget(DbConnection? connection)
    {
        if (connection is not null)
        {
            var state = State.GetOrCreateValue(connection);
            state.Values = null;
            state.Fresh = false;
        }
    }

    private void Sync(DbCommand command, CommandEventData eventData)
    {
        if (eventData.CommandSource is CommandSource.Migrations or CommandSource.SaveChanges)
        {
            Separately(command, eventData.Context);
            return;
        }
        Prefix(command, eventData.Context);
    }

    private Task SyncAsync(DbCommand command, CommandEventData eventData, CancellationToken ct)
    {
        if (eventData.CommandSource is CommandSource.Migrations or CommandSource.SaveChanges)
        {
            return SeparatelyAsync(command, eventData.Context, ct);
        }
        Prefix(command, eventData.Context);
        return Task.CompletedTask;
    }

    private void Prefix(DbCommand command, DbContext? context)
    {
        if (command.Connection is not { } connection || Current(connection, context) is not { } desired)
        {
            return;
        }
        command.CommandText = Statement(desired) + command.CommandText;
        Record(connection, desired);
    }

    /// <summary>What to set before this connection's next command; null when it already holds it. A
    /// session given nothing already holds "no tenant, no scope" (the policies read empty as unset).</summary>
    private static (string Tenant, string Scope)? Current(DbConnection connection, DbContext? context)
    {
        var desired = Desired(context);
        var state = State.GetOrCreateValue(connection);
        return state.Values == desired || (state.Fresh && desired == (string.Empty, string.Empty)) ? null : desired;
    }

    // Untraced: before 2026-10-23 this ran on every connection open, and each span cost again in Alloy
    // and Tempo while saying only "a connection was opened". The SDK's sampler drops what starts inside
    // the scope. Migrations and SaveChanges only now.
    private void Separately(DbCommand command, DbContext? context)
    {
        if (command.Connection is not { } connection || Current(connection, context) is not { } desired)
        {
            return;
        }
        using (SuppressInstrumentationScope.Begin())
        using (var set = Command(connection, command.Transaction, desired))
        {
            set.ExecuteNonQuery();
        }
        Record(connection, desired);
    }

    private async Task SeparatelyAsync(DbCommand command, DbContext? context, CancellationToken ct)
    {
        if (command.Connection is not { } connection || Current(connection, context) is not { } desired)
        {
            return;
        }
        using (SuppressInstrumentationScope.Begin())
        await using (var set = Command(connection, command.Transaction, desired))
        {
            await set.ExecuteNonQueryAsync(ct);
        }
        Record(connection, desired);
    }

    private static string Statement((string Tenant, string Scope) values)
        => $"{TenantSet}{values.Tenant}'; SET app.scope = '{values.Scope}'; ";

    private static DbCommand Command(DbConnection connection, DbTransaction? transaction, (string Tenant, string Scope) values)
    {
        var command = connection.CreateCommand();
        command.CommandText = Statement(values);
        command.Transaction = transaction;
        return command;
    }

    private void Record(DbConnection connection, (string Tenant, string Scope) values)
    {
        var state = State.GetOrCreateValue(connection);
        state.Values = values;
        state.Fresh = false;
        // Debug, not information: this runs per unit of work. It is here for the day a page
        // comes back blank under enforcement, when "which tenant did the database think this
        // was" is the first question and nothing else answers it.
        logger.LogDebug("RLS scope set: tenant={Tenant} scope={Scope}", values.Tenant, values.Scope);
    }
}
