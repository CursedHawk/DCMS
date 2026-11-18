using Dcms.PluginSdk.Abstractions;
using Dcms.Shared.Data.Chat;
using Dcms.Shared.Data.Cms;
using Dcms.Shared.Data.Forms;
using Dcms.Shared.Data.Tenancy;
using Dcms.Shared.Data.Visitors;
using Microsoft.EntityFrameworkCore;

using Dcms.Shared.Data;
namespace Dcms.AdminApi.Environments.Copiers;

// rls: every read runs inside copy.InSource() and every write inside copy.InTarget(), each one
// environment's scope; IgnoreTenantFilter keeps the environment filter on.

/// <summary>
/// Content the configuration copy did not bring: unpublished items and their drafts, with every
/// version. Items the target has already (published ones, by key) are left as they are, and
/// references to them point at the target's.
/// </summary>
public sealed class ContentDraftsCopier(CmsDbContext db) : IEnvironmentDataCopier
{
    public string Scope => "content-drafts";

    public int Order => 10;

    public async Task<int> CopyAsync(EnvironmentDataCopy copy, CancellationToken ct)
    {
        Dictionary<(Guid, string, string), Guid> existing;
        List<ContentItem> items;
        using (copy.InTarget())
        {
            existing = (await db.ContentItems.IgnoreTenantFilter().AsNoTracking()
                    .Where(c => c.TenantId == copy.TenantId && c.EnvironmentId == copy.TargetEnvironmentId)
                    .Select(c => new { c.PluginInstanceId, c.ContentType, c.Slug, c.Id }).ToListAsync(ct))
                .ToDictionary(c => (c.PluginInstanceId, c.ContentType, c.Slug), c => c.Id);
        }
        using (copy.InSource())
        {
            items = await db.ContentItems.IgnoreTenantFilter().AsNoTracking()
                .Where(c => c.TenantId == copy.TenantId && c.EnvironmentId == copy.SourceEnvironmentId)
                .ToListAsync(ct);
        }
        // Every item's id first, so a draft refers to the target's item whichever comes first.
        var copied = new HashSet<Guid>();
        foreach (var item in items.Where(i => copy.Instances.ContainsKey(i.PluginInstanceId)))
        {
            if (existing.TryGetValue((copy.Instances[item.PluginInstanceId], item.ContentType, item.Slug), out var there))
            {
                copy.Ids[item.Id] = there;
            }
            else
            {
                copy.NewId(item.Id);
                copied.Add(item.Id);
            }
        }

        var count = await EnvironmentRows.CopyAsync(db,
            db.ContentItems.IgnoreTenantFilter()
                .Where(c => c.TenantId == copy.TenantId && c.EnvironmentId == copy.SourceEnvironmentId).OrderBy(c => c.Id),
            copy.InSource, copy.InTarget, c =>
            {
                if (!copied.Contains(c.Id)) return false;
                (c.Id, c.EnvironmentId, c.PluginInstanceId) = (copy.Map(c.Id), copy.TargetEnvironmentId, copy.Map(c.PluginInstanceId));
                c.CurrentDraftVersionId = c.CurrentDraftVersionId is { } d ? copy.NewId(d) : null;
                c.PublishedVersionId = c.PublishedVersionId is { } p ? copy.NewId(p) : null;
                return true;
            }, ct);
        count += await EnvironmentRows.CopyAsync(db,
            db.ContentVersions.IgnoreTenantFilter()
                .Where(v => v.TenantId == copy.TenantId && v.EnvironmentId == copy.SourceEnvironmentId).OrderBy(v => v.Id),
            copy.InSource, copy.InTarget, v =>
            {
                if (!copied.Contains(v.ItemId)) return false;
                (v.Id, v.EnvironmentId, v.ItemId, v.DataJson) = (copy.NewId(v.Id), copy.TargetEnvironmentId, copy.Map(v.ItemId), copy.Remap(v.DataJson));
                return true;
            }, ct);
        return count;
    }
}

/// <summary>What plugins keep for themselves (<c>dcms.storage</c>), under the target's instances. Secrets are not copied.</summary>
public sealed class PluginDataCopier(CmsDbContext db) : IEnvironmentDataCopier
{
    public string Scope => "plugin-data";

    public int Order => 30;

    public async Task<int> CopyAsync(EnvironmentDataCopy copy, CancellationToken ct)
    {
        HashSet<(Guid?, string, string)> existing;
        using (copy.InTarget())
        {
            existing = (await db.PluginData.IgnoreTenantFilter().AsNoTracking()
                    .Where(d => d.TenantId == copy.TenantId && d.EnvironmentId == copy.TargetEnvironmentId)
                    .Select(d => new { d.InstanceId, d.Collection, d.Key }).ToListAsync(ct))
                .Select(d => (d.InstanceId, d.Collection, d.Key)).ToHashSet();
        }
        return await EnvironmentRows.CopyAsync(db,
            db.PluginData.IgnoreTenantFilter()
                .Where(d => d.TenantId == copy.TenantId && d.EnvironmentId == copy.SourceEnvironmentId).OrderBy(d => d.Id),
            copy.InSource, copy.InTarget, d =>
            {
                Guid? instance = d.InstanceId is { } i ? copy.Instances.GetValueOrDefault(i) : null;
                if (d.InstanceId is not null && instance is null) return false;
                if (existing.Contains((instance, d.Collection, d.Key))) return false;
                (d.Id, d.EnvironmentId, d.InstanceId, d.DataJson) = (copy.NewId(d.Id), copy.TargetEnvironmentId, instance, copy.Remap(d.DataJson));
                return true;
            }, ct);
    }
}

/// <summary>Visitor accounts, with their passwords; not their sessions.</summary>
public sealed class VisitorsCopier(VisitorsDbContext db) : IEnvironmentDataCopier
{
    public string Scope => "visitors";

    public int Order => 40;

    public bool PersonalData => true;

    public Task<int> CopyAsync(EnvironmentDataCopy copy, CancellationToken ct) =>
        EnvironmentRows.CopyAsync(db,
            db.Accounts.IgnoreTenantFilter()
                .Where(a => a.TenantId == copy.TenantId && a.EnvironmentId == copy.SourceEnvironmentId).OrderBy(a => a.Id),
            copy.InSource, copy.InTarget, a =>
            {
                (a.Id, a.EnvironmentId) = (copy.NewId(a.Id), copy.TargetEnvironmentId);
                return true;
            }, ct);
}

public sealed class FormSubmissionsCopier(FormsDbContext db) : IEnvironmentDataCopier
{
    public string Scope => "form-submissions";

    public int Order => 50;

    public bool PersonalData => true;

    public Task<int> CopyAsync(EnvironmentDataCopy copy, CancellationToken ct) =>
        EnvironmentRows.CopyAsync(db,
            db.Submissions.IgnoreTenantFilter()
                .Where(s => s.TenantId == copy.TenantId && s.EnvironmentId == copy.SourceEnvironmentId).OrderBy(s => s.Id),
            copy.InSource, copy.InTarget, s =>
            {
                if (!copy.Instances.TryGetValue(s.PluginInstanceId, out var instance)) return false;
                (s.Id, s.EnvironmentId, s.PluginInstanceId, s.DataJson) = (copy.NewId(s.Id), copy.TargetEnvironmentId, instance, copy.Remap(s.DataJson));
                s.VisitorId = s.VisitorId is { } v ? copy.Map(v) : null;
                return true;
            }, ct);
}

public sealed class ChatCopier(ChatDbContext db) : IEnvironmentDataCopier
{
    public string Scope => "chat";

    public int Order => 60;

    public bool PersonalData => true;

    public async Task<int> CopyAsync(EnvironmentDataCopy copy, CancellationToken ct) =>
        await EnvironmentRows.CopyAsync(db,
            db.Conversations.IgnoreTenantFilter()
                .Where(c => c.TenantId == copy.TenantId && c.EnvironmentId == copy.SourceEnvironmentId).OrderBy(c => c.Id),
            copy.InSource, copy.InTarget, c =>
            {
                (c.Id, c.EnvironmentId) = (copy.NewId(c.Id), copy.TargetEnvironmentId);
                c.VisitorId = c.VisitorId is { } v ? copy.Map(v) : null;
                return true;
            }, ct)
        + await EnvironmentRows.CopyAsync(db,
            db.Messages.IgnoreTenantFilter()
                .Where(m => m.TenantId == copy.TenantId && m.EnvironmentId == copy.SourceEnvironmentId).OrderBy(m => m.Id),
            copy.InSource, copy.InTarget, m =>
            {
                (m.Id, m.EnvironmentId, m.ConversationId) = (copy.NewId(m.Id), copy.TargetEnvironmentId, copy.Map(m.ConversationId));
                return true;
            }, ct);
}
