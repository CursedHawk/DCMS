using System.Text.Json;
using System.Text.Json.Nodes;
using Dcms.PluginSdk.Abstractions;
using Dcms.Shared.Contracts.Events;
using Dcms.Shared.Contracts.Messaging;
using Dcms.Shared.Data.Cms;
using Dcms.Shared.Security;
using Microsoft.EntityFrameworkCore;

using Dcms.Shared.Data;
namespace Dcms.AdminApi.Environments.Kinds;

/// <summary>
/// Published content as a kind (ADR 0028): an item keyed <c>{appSlug}/{type}/{slug}</c>, its value
/// the published version's data. Applying publishes it through the ordinary path -- a new version,
/// the outbox, the site build after it; a removal unpublishes, never deletes. Drafts stay each
/// environment's own.
///
/// <para>A reference to another content item is the source's id, which means nothing in the
/// target: it is exported as <c>@content:{key}</c> and resolved to the target's item of that key on
/// apply, created as an empty draft when the target does not have it yet (the same deployment
/// usually brings it). Media ids pass unchanged: the library is the workspace's.</para>
/// </summary>
// rls: EnvironmentMigrator calls every kind inside RlsScope.Environment, the environment it works
// on; IgnoreTenantFilter drops only the tenant filter, so queries stay in that environment.
public sealed class ContentConfigKind(CmsDbContext db) : IEnvironmentConfigKind
{
    public const string RefPrefix = "@content:";

    public string Kind => "content";

    public string? Permission => PlatformPermissions.ContentPublish;

    // After plugins (20), whose instances hold the content.
    public int Order => 60;

    public async Task<IReadOnlyList<EnvironmentConfigItem>> ExportAsync(Guid tenantId, CancellationToken ct)
    {
        var keys = await KeysAsync(tenantId, ct);
        // ponytail: every published item's data on every export; page or hash it if a workspace's content outgrows memory.
        var published = await db.ContentItems.IgnoreTenantFilter().AsNoTracking()
            .Where(c => c.TenantId == tenantId && c.PublishedVersionId != null)
            .Join(db.ContentVersions.IgnoreTenantFilter(), c => c.PublishedVersionId, v => v.Id, (c, v) => new { c.Id, v.DataJson })
            .ToListAsync(ct);
        return published.Where(p => keys.ContainsKey(p.Id))
            .Select(p => new EnvironmentConfigItem(keys[p.Id],
                JsonSerializer.SerializeToElement(Mark(JsonNode.Parse(p.DataJson), keys))) { Label = keys[p.Id] })
            .ToList();
    }

    public async Task ApplyAsync(Guid tenantId, string key, JsonElement? value, EnvironmentApplyContext context, CancellationToken ct)
    {
        var item = await FindAsync(tenantId, key, ct);
        if (value is null)
        {
            // Unpublished in the source: unpublished here too. Deleting is each environment's call.
            if (item?.PublishedVersionId is null) return;
            item.PublishedVersionId = null;
            item.Status = ContentStatus.Draft;
            item.PublishedAt = null;
            item.UpdatedAt = DateTimeOffset.UtcNow;
            Outbox(tenantId, Subjects.ContentUnpublished, new ContentUnpublished(Guid.NewGuid(), DateTimeOffset.UtcNow,
                tenantId, item.PluginInstanceId, item.Id, item.ContentType, item.Slug));
            await db.SaveChangesAsync(ct);
            return;
        }

        item ??= await PlaceholderAsync(tenantId, key, ct);
        var data = await ResolveAsync(tenantId, JsonNode.Parse(value.Value.GetRawText()), ct);
        var next = (await db.ContentVersions.IgnoreTenantFilter()
            .Where(v => v.TenantId == tenantId && v.ItemId == item.Id).MaxAsync(v => (int?)v.VersionNo, ct) ?? 0) + 1;
        var version = new ContentVersion
        {
            Id = Guid.NewGuid(), TenantId = tenantId, ItemId = item.Id, VersionNo = next, DataJson = data?.ToJsonString() ?? "{}",
        };
        db.ContentVersions.Add(version);
        item.CurrentDraftVersionId = version.Id;
        item.PublishedVersionId = version.Id;
        item.Status = ContentStatus.Published;
        item.PublishedAt = DateTimeOffset.UtcNow;
        item.UpdatedAt = item.PublishedAt.Value;
        Outbox(tenantId, Subjects.ContentPublished, new ContentPublished(Guid.NewGuid(), DateTimeOffset.UtcNow,
            tenantId, item.PluginInstanceId, item.Id, item.ContentType, item.Slug));
        await db.SaveChangesAsync(ct);
    }

    private void Outbox<T>(Guid tenantId, string subject, T evt) =>
        db.Outbox.Add(new ContentOutboxMessage
        {
            Id = Guid.NewGuid(), TenantId = tenantId, Subject = subject, PayloadJson = JsonSerializer.Serialize(evt),
        });

    /// <summary>Every item's key across environments, by its id here.</summary>
    private async Task<Dictionary<Guid, string>> KeysAsync(Guid tenantId, CancellationToken ct)
    {
        var slugs = await db.PluginInstances.IgnoreTenantFilter().AsNoTracking()
            .Where(p => p.TenantId == tenantId).ToDictionaryAsync(p => p.Id, p => p.Slug, ct);
        return (await db.ContentItems.IgnoreTenantFilter().AsNoTracking()
                .Where(c => c.TenantId == tenantId)
                .Select(c => new { c.Id, c.PluginInstanceId, c.ContentType, c.Slug })
                .ToListAsync(ct))
            .Where(c => slugs.ContainsKey(c.PluginInstanceId))
            .ToDictionary(c => c.Id, c => $"{slugs[c.PluginInstanceId]}/{c.ContentType}/{c.Slug}");
    }

    /// <summary>The data with every content item's id replaced by its key.</summary>
    internal static JsonNode? Mark(JsonNode? node, IReadOnlyDictionary<Guid, string> keys) => node switch
    {
        JsonObject o => new JsonObject(o.Select(p => KeyValuePair.Create(p.Key, Mark(p.Value, keys)))),
        JsonArray a => new JsonArray(a.Select(n => Mark(n, keys)).ToArray()),
        JsonValue v when v.TryGetValue<string>(out var s) && Guid.TryParse(s, out var id) && keys.TryGetValue(id, out var key)
            => JsonValue.Create(RefPrefix + key),
        _ => node?.DeepClone(),
    };

    /// <summary>The data with every key put back as this environment's item of that key.</summary>
    private async Task<JsonNode?> ResolveAsync(Guid tenantId, JsonNode? node, CancellationToken ct)
    {
        switch (node)
        {
            case JsonObject o:
                var obj = new JsonObject();
                foreach (var (name, child) in o.ToList()) obj[name] = await ResolveAsync(tenantId, child, ct);
                return obj;
            case JsonArray a:
                var arr = new JsonArray();
                foreach (var child in a.ToList()) arr.Add(await ResolveAsync(tenantId, child, ct));
                return arr;
            case JsonValue v when v.TryGetValue<string>(out var s) && s.StartsWith(RefPrefix, StringComparison.Ordinal):
                var key = s[RefPrefix.Length..];
                var target = await FindAsync(tenantId, key, ct) ?? await PlaceholderAsync(tenantId, key, ct);
                return JsonValue.Create(target.Id.ToString());
            default:
                return node?.DeepClone();
        }
    }

    private async Task<ContentItem?> FindAsync(Guid tenantId, string key, CancellationToken ct)
    {
        var (app, type, slug) = Split(key);
        return await db.ContentItems.IgnoreTenantFilter()
            .Where(c => c.TenantId == tenantId && c.ContentType == type && c.Slug == slug
                        && db.PluginInstances.IgnoreTenantFilter().Any(p => p.Id == c.PluginInstanceId && p.Slug == app))
            .FirstOrDefaultAsync(ct);
    }

    /// <summary>An empty draft to publish into, or for a reference to point at until its own change arrives.</summary>
    private async Task<ContentItem> PlaceholderAsync(Guid tenantId, string key, CancellationToken ct)
    {
        var (app, type, slug) = Split(key);
        var instance = await db.PluginInstances.IgnoreTenantFilter()
                           .FirstOrDefaultAsync(p => p.TenantId == tenantId && p.Slug == app, ct)
                       ?? throw new EnvironmentConfigException($"there is no app '{app}' here for '{key}'; deploy the app first");
        var item = new ContentItem
        {
            Id = Guid.NewGuid(), TenantId = tenantId, PluginInstanceId = instance.Id, ContentType = type, Slug = slug,
            Status = ContentStatus.Draft,
        };
        db.ContentItems.Add(item);
        await db.SaveChangesAsync(ct);
        return item;
    }

    private static (string App, string Type, string Slug) Split(string key)
    {
        var parts = key.Split('/', 3);
        return parts.Length == 3 ? (parts[0], parts[1], parts[2]) : throw new EnvironmentConfigException($"'{key}' is not a content key");
    }
}
