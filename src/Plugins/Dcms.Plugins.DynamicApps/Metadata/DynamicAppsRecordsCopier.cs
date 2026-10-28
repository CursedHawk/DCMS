using Dcms.PluginSdk.Abstractions;
using Dcms.Shared.Data.DynamicApps;
using Dcms.Shared.Data.Tenancy;
using Microsoft.EntityFrameworkCore;

using Dcms.Shared.Data;
namespace Dcms.Plugins.DynamicApps.Metadata;

/// <summary>
/// Duplicating an environment with its Dynamic Apps records (ADR 0028): every record, its
/// relationship links and its unique keys, under the target's application instances. Tables,
/// fields, relationships and constraints keep their ids across environments (the model is
/// deployed as it is), so only records get new ones, and a record's data has every id it holds
/// of another record remapped.
/// </summary>
// rls: reads inside copy.InSource(), writes inside copy.InTarget(); IgnoreTenantFilter keeps the
// environment filter on.
internal sealed class DynamicAppsRecordsCopier(AppsDbContext apps) : IEnvironmentDataCopier
{
    public string Scope => "dynamic-apps-records";

    // After visitors (40), whom a record created from the site names as its owner.
    public int Order => 45;

    public async Task<int> CopyAsync(EnvironmentDataCopy copy, CancellationToken ct)
    {
        IQueryable<T> Source<T>(DbSet<T> set) where T : EnvironmentEntity =>
            set.IgnoreTenantFilter().Where(x => x.TenantId == copy.TenantId && x.EnvironmentId == copy.SourceEnvironmentId).OrderBy(x => x.Id);

        // Every record's new id first: a lookup may name a record that comes later.
        using (copy.InSource())
        {
            foreach (var id in await Source(apps.Records).Where(r => copy.Instances.Keys.Contains(r.InstanceId)).Select(r => r.Id).ToListAsync(ct))
            {
                copy.NewId(id);
            }
        }

        var count = await EnvironmentRows.CopyAsync(apps, Source(apps.Records), copy.InSource, copy.InTarget, r =>
        {
            if (!copy.Instances.TryGetValue(r.InstanceId, out var instance)) return false;
            (r.Id, r.EnvironmentId, r.InstanceId, r.Data) = (copy.Map(r.Id), copy.TargetEnvironmentId, instance, copy.Remap(r.Data));
            r.OwnerVisitorId = r.OwnerVisitorId is { } v ? copy.Map(v) : null;
            return true;
        }, ct);
        count += await EnvironmentRows.CopyAsync(apps, Source(apps.RelationLinks), copy.InSource, copy.InTarget, l =>
        {
            if (!copy.Instances.TryGetValue(l.InstanceId, out var instance) || !copy.Ids.ContainsKey(l.SourceId) || !copy.Ids.ContainsKey(l.TargetId)) return false;
            (l.Id, l.EnvironmentId, l.InstanceId, l.SourceId, l.TargetId) =
                (Guid.NewGuid(), copy.TargetEnvironmentId, instance, copy.Ids[l.SourceId], copy.Ids[l.TargetId]);
            return true;
        }, ct);
        count += await EnvironmentRows.CopyAsync(apps, Source(apps.UniqueKeys), copy.InSource, copy.InTarget, k =>
        {
            if (!copy.Instances.TryGetValue(k.InstanceId, out var instance) || !copy.Ids.ContainsKey(k.RecordId)) return false;
            (k.Id, k.EnvironmentId, k.InstanceId, k.RecordId) = (Guid.NewGuid(), copy.TargetEnvironmentId, instance, copy.Ids[k.RecordId]);
            return true;
        }, ct);
        return count;
    }
}
