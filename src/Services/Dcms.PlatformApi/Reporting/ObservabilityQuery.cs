using System.Data.Common;
using Dcms.Shared.Data.Platform;
using Microsoft.EntityFrameworkCore;

namespace Dcms.PlatformApi.Reporting;

/// <summary>
/// A thin reader over the <c>obs.*</c> reporting views.
///
/// <para><b>Plain ADO rather than EF, deliberately.</b> These are thirteen read-only views
/// owned by another service's migrations, and mapping them as entities would put their column
/// list into this context's model — where a column renamed upstream becomes a startup-time
/// model error in a service that only ever wanted to read a number, and where EF's snapshot
/// starts tracking objects platform-api does not own. The views are the contract; SQL against
/// them is the honest expression of that.</para>
///
/// <para>Every query here is a SELECT through the least-privilege <c>dcms_platform</c> role,
/// which holds USAGE on <c>obs</c> and nothing else. The views execute with their owner's
/// rights, which is what lets them report across every tenant without this role holding a
/// grant on a single base table.</para>
///
/// <para>Parameters are always bound, never interpolated. Nothing here takes a table or
/// column name from a caller.</para>
/// </summary>
public sealed class ObservabilityQuery(PlatformDbContext db)
{
    /// <summary>
    /// Runs <paramref name="sql"/> and projects each row with <paramref name="map"/>.
    /// </summary>
    public async Task<List<T>> QueryAsync<T>(
        string sql,
        Func<DbDataReader, T> map,
        IReadOnlyDictionary<string, object?>? parameters = null,
        CancellationToken ct = default)
    {
        var connection = db.Database.GetDbConnection();
        var opened = false;
        if (connection.State != System.Data.ConnectionState.Open)
        {
            await db.Database.OpenConnectionAsync(ct);
            opened = true;
        }

        try
        {
            await using var command = connection.CreateCommand();
            command.CommandText = sql;
            if (parameters is not null)
            {
                foreach (var (name, value) in parameters)
                {
                    var p = command.CreateParameter();
                    p.ParameterName = name;
                    p.Value = value ?? DBNull.Value;
                    command.Parameters.Add(p);
                }
            }

            await using var reader = await command.ExecuteReaderAsync(ct);
            var rows = new List<T>();
            while (await reader.ReadAsync(ct))
            {
                rows.Add(map(reader));
            }
            return rows;
        }
        finally
        {
            if (opened)
            {
                await db.Database.CloseConnectionAsync();
            }
        }
    }

    /// <summary>Single-row helper; returns <c>default</c> when the query yields nothing.</summary>
    public async Task<T?> SingleAsync<T>(
        string sql,
        Func<DbDataReader, T> map,
        IReadOnlyDictionary<string, object?>? parameters = null,
        CancellationToken ct = default)
    {
        var rows = await QueryAsync(sql, map, parameters, ct);
        return rows.Count == 0 ? default : rows[0];
    }
}

/// <summary>Null-tolerant readers. Every one of these views can produce a NULL aggregate.</summary>
public static class DbDataReaderExtensions
{
    public static long GetInt64OrZero(this DbDataReader reader, string column)
    {
        var i = reader.GetOrdinal(column);
        return reader.IsDBNull(i) ? 0L : Convert.ToInt64(reader.GetValue(i));
    }

    public static int GetInt32OrZero(this DbDataReader reader, string column)
    {
        var i = reader.GetOrdinal(column);
        return reader.IsDBNull(i) ? 0 : Convert.ToInt32(reader.GetValue(i));
    }

    public static string? GetStringOrNull(this DbDataReader reader, string column)
    {
        var i = reader.GetOrdinal(column);
        return reader.IsDBNull(i) ? null : reader.GetString(i);
    }

    public static DateTimeOffset? GetDateTimeOrNull(this DbDataReader reader, string column)
    {
        var i = reader.GetOrdinal(column);
        if (reader.IsDBNull(i)) return null;
        var value = reader.GetValue(i);
        return value switch
        {
            DateTimeOffset dto => dto,
            DateTime dt => new DateTimeOffset(DateTime.SpecifyKind(dt, DateTimeKind.Utc)),
            _ => null,
        };
    }

    public static Guid? GetGuidOrNull(this DbDataReader reader, string column)
    {
        var i = reader.GetOrdinal(column);
        return reader.IsDBNull(i) ? null : reader.GetGuid(i);
    }
}
