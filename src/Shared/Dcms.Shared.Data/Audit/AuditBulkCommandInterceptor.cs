using System.Data.Common;
using System.Text.RegularExpressions;
using Dcms.Shared.Audit;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace Dcms.Shared.Data.Audit;

/// <summary>
/// Records set-based statements — <c>ExecuteUpdateAsync</c> and <c>ExecuteDeleteAsync</c> —
/// which never touch the change tracker and so are invisible to
/// <see cref="AuditChangeCapture"/>.
///
/// <para>There are forty-seven such call sites, and between them they destroy most of what the
/// platform can destroy: a tenant purge, a site deletion, a sandbox reset, a bulk media delete.
/// Leaving them uncovered would mean the operations with the least recoverable consequences were
/// the ones with no trail.</para>
///
/// <para>What it can say is limited and the record says so. There is no before-image: the rows
/// are gone by the time the statement returns, and reading them first would turn one statement
/// into two and change the concurrency of every delete on the platform to serve the log. So the
/// record carries the table, the shape of the statement, and how many rows it affected — enough
/// to establish that something large happened, when, and at whose hand, and to point an
/// investigator at the backup that still has the rows.</para>
///
/// <para>Callers that know better should say so. A block wrapped in
/// <c>using var _ = scope.SuppressBulkCapture()</c> takes on the duty of recording one
/// meaningful record instead — which is what the tenant purge does, since twenty-five rows
/// saying a table got shorter tell an investigator less than one saying what was destroyed.</para>
/// </summary>
public sealed partial class AuditBulkCommandInterceptor(IAuditRecorder recorder, AuditScope scope)
    : DbCommandInterceptor
{
    /// <summary>Enough of the statement to identify it; never enough to be a data leak.</summary>
    private const int MaxSqlLength = 400;

    public override int NonQueryExecuted(DbCommand command, CommandExecutedEventData eventData, int result)
    {
        Record(command, eventData, result);
        return result;
    }

    public override ValueTask<int> NonQueryExecutedAsync(
        DbCommand command,
        CommandExecutedEventData eventData,
        int result,
        CancellationToken cancellationToken = default)
    {
        Record(command, eventData, result);
        return ValueTask.FromResult(result);
    }

    private void Record(DbCommand command, CommandExecutedEventData eventData, int rows)
    {
        // CommandSource is the only reliable way to tell a set-based statement from the
        // ordinary INSERTs and UPDATEs that SaveChanges issues through the same path — and
        // those are already recorded, in full, with a diff.
        if (eventData.CommandSource is not (CommandSource.ExecuteUpdate or CommandSource.ExecuteDelete))
        {
            return;
        }

        if (scope.BulkCaptureSuppressions > 0)
        {
            return;
        }

        // A statement that matched nothing is not an event. Recording it would fill the log
        // with the cleanup passes that run whether or not there is anything to clean up.
        if (rows == 0)
        {
            return;
        }

        var deleted = eventData.CommandSource == CommandSource.ExecuteDelete;
        var sql = command.CommandText ?? string.Empty;

        recorder.Record(deleted ? AuditActions.BulkDeleted : AuditActions.BulkUpdated)
            .For("table", TableOf(sql))
            .With("rows", rows)
            // Parameters are deliberately not recorded: they are the tenant's data, and the
            // statement text alone already says which table and which shape of predicate.
            .With("sql", sql.Length <= MaxSqlLength ? sql : sql[..MaxSqlLength] + "…")
            .With("before_values", "unavailable: a set-based statement never loads the rows it changes");
    }

    /// <summary>
    /// The table the statement targets, for the resource id. Best-effort by design — this is a
    /// label to search on, and a record naming an unparsed statement still beats no record.
    /// </summary>
    private static string TableOf(string sql)
    {
        var match = TablePattern().Match(sql);
        return match.Success ? match.Groups[1].Value.Replace("\"", string.Empty, StringComparison.Ordinal) : "unknown";
    }

    [GeneratedRegex(@"(?:DELETE\s+FROM|UPDATE)\s+((?:""[^""]+""\.)?""[^""]+"")",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex TablePattern();
}
