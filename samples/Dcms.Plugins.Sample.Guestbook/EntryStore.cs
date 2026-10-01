using System.Text.Json;
using Dcms.Plugins.Sample.Guestbook.Api;
using Dcms.PluginSdk.Abstractions;
using Dcms.PluginSdk.Abstractions.Contracts;
using Dcms.PluginSdk.Abstractions.Platform;

namespace Dcms.Plugins.Sample.Guestbook;

/// <summary>
/// The guestbook's data, in <c>dcms.storage</c> — so the plugin needs no table, migration or
/// RLS entry, and installs into any DCMS by copying a folder. The platform stamps tenant and
/// plugin onto every document; this class only decides collections and keys.
///
/// <para>Entries are plugin-wide documents in one collection per instance
/// (<c>entries-{instanceId}</c>), not instance-scoped ones: the tenant-wide overview screen and
/// the daily digest job run without an instance and still need to read every guestbook.</para>
/// </summary>
public sealed class EntryStore(IPluginContext context)
{
    private const string InstancesCollection = "guestbooks";

    private IPluginStorage Storage => context.Contracts.Get<IPluginStorage>();

    public static string Collection(Guid instanceId) => $"entries-{instanceId:N}";

    /// <summary>
    /// Storage lists a collection in key order. A key that counts DOWN with time makes that
    /// order newest-first with no index of our own; the suffix keeps two entries in one tick apart.
    /// </summary>
    public static string NewId(DateTimeOffset now) =>
        (DateTimeOffset.MaxValue.UtcTicks - now.UtcTicks).ToString("D19") + Guid.NewGuid().ToString("N")[..8];

    public async Task<(IReadOnlyList<GuestbookEntry> Entries, long Total)> ListAsync(
        Guid instanceId, EntryStatus? status, int page, int pageSize, CancellationToken ct)
    {
        var where = status is { } s
            ? new Dictionary<string, JsonElement> { ["status"] = JsonSerializer.SerializeToElement(s, GuestbookJson.Options) }
            : null;
        var result = await Storage.QueryAsync(
            new QueryDocuments(Collection(instanceId), where, page, Math.Clamp(pageSize, 1, 100), InstanceScoped: false), ct);
        return (result.Items.Select(ToEntry).ToList(), result.TotalCount);
    }

    public async Task<long> CountAsync(Guid instanceId, EntryStatus status, CancellationToken ct) =>
        (await ListAsync(instanceId, status, 1, 1, ct)).Total;

    public async Task<GuestbookEntry?> GetAsync(Guid instanceId, string id, CancellationToken ct) =>
        await Storage.GetAsync(new DocumentAddress(Collection(instanceId), id, InstanceScoped: false), ct) is { } doc
            ? ToEntry(doc)
            : null;

    public Task SaveAsync(Guid instanceId, GuestbookEntry entry, CancellationToken ct) =>
        Storage.PutAsync(new PutDocument(Collection(instanceId), entry.Id,
            JsonSerializer.SerializeToElement(entry, GuestbookJson.Options), InstanceScoped: false), ct);

    public async Task<bool> DeleteAsync(Guid instanceId, string id, CancellationToken ct) =>
        (await Storage.DeleteAsync(new DocumentAddress(Collection(instanceId), id, InstanceScoped: false), ct)).Found;

    /// <summary>Remembers a guestbook (its name and slug) for code that runs without an instance.</summary>
    public Task RememberAsync(PluginInstanceContext instance, CancellationToken ct) =>
        Storage.PutAsync(new PutDocument(InstancesCollection, instance.InstanceId.ToString("N"),
            JsonSerializer.SerializeToElement(new KnownGuestbook(instance.InstanceId, instance.Slug, instance.Name)),
            InstanceScoped: false), ct);

    public async Task<IReadOnlyList<KnownGuestbook>> KnownAsync(CancellationToken ct) =>
        (await Storage.QueryAsync(new QueryDocuments(InstancesCollection, PageSize: 100, InstanceScoped: false), ct))
        .Items.Select(d => d.Data.Deserialize<KnownGuestbook>()!).ToList();

    private static GuestbookEntry ToEntry(StoredDocument doc) => doc.Data.Deserialize<GuestbookEntry>(GuestbookJson.Options)!;
}

public sealed record KnownGuestbook(Guid InstanceId, string Slug, string Name);
