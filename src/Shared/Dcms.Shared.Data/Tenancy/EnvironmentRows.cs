using Microsoft.EntityFrameworkCore;

namespace Dcms.Shared.Data.Tenancy;

/// <summary>
/// Copies one table's rows from one environment into another (ADR 0028): read a page inside the
/// source's database scope, give each row its new keys, write the page inside the target's. The
/// row-level security policy admits one environment per statement, so a row never crosses in one.
/// </summary>
public static class EnvironmentRows
{
    /// <param name="source">The rows to copy, ordered so that paging is stable.</param>
    /// <param name="rekey">Points a read row at the target (new id, tenant, environment, references); false skips it.</param>
    /// <returns>How many rows were written.</returns>
    public static async Task<int> CopyAsync<T>(
        DbContext db, IQueryable<T> source, Func<IDisposable> inSource, Func<IDisposable> inTarget,
        Func<T, bool> rekey, CancellationToken ct, int pageSize = 1000) where T : class
    {
        var copied = 0;
        for (var skip = 0; ; skip += pageSize)
        {
            // ponytail: offset paging, fine to a few hundred thousand rows; keyset if one table outgrows it.
            List<T> page;
            using (inSource())
            {
                page = await source.AsNoTracking().Skip(skip).Take(pageSize).ToListAsync(ct);
            }
            if (page.Count == 0) return copied;
            var kept = page.Where(rekey).ToList();
            using (inTarget())
            {
                db.Set<T>().AddRange(kept);
                await db.SaveChangesAsync(ct);
                db.ChangeTracker.Clear();
            }
            copied += kept.Count;
            if (page.Count < pageSize) return copied;
        }
    }
}
