using Microsoft.EntityFrameworkCore;

namespace Dcms.Shared.Data.Tenancy;

/// <summary>
/// Copies one table's rows from one environment into another (ADR 0028): read a page inside the
/// source's database scope, give each row its new keys, write the page inside the target's. The
/// row-level security policy admits one environment per statement, so a row never crosses in one.
/// </summary>
public static class EnvironmentRows
{
    /// <param name="source">The rows to copy, ordered by <c>Id</c>: pages continue after the last id copied.</param>
    /// <param name="rekey">Points a read row at the target (new id, tenant, environment, references); false skips it.</param>
    /// <returns>How many rows were written.</returns>
    public static async Task<int> CopyAsync<T>(
        DbContext db, IQueryable<T> source, Func<IDisposable> inSource, Func<IDisposable> inTarget,
        Func<T, bool> rekey, CancellationToken ct, int pageSize = 1000) where T : TenantEntity
    {
        var copied = 0;
        Guid? after = null;
        while (true)
        {
            // Keyset, not offset: the source stays live while it is copied (a flow adding records),
            // and a row added before the offset would shift the next page onto one already copied.
            List<T> page;
            using (inSource())
            {
                var rest = after is { } last ? source.Where(x => x.Id.CompareTo(last) > 0) : source;
                page = await rest.AsNoTracking().Take(pageSize).ToListAsync(ct);
            }
            if (page.Count == 0) return copied;
            after = page[^1].Id; // before rekey gives it its new one
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
