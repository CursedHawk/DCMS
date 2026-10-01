using System.Text.Json;
using Dcms.Plugins.Blog.Api;
using Dcms.Plugins.Sample.Guestbook.Api;
using Dcms.Plugins.VisitorAuth.Api;
using Dcms.PluginSdk.Abstractions;
using Dcms.PluginSdk.Abstractions.Contracts;
using Dcms.PluginSdk.Abstractions.Platform;
using Microsoft.AspNetCore.Http;

namespace Dcms.Plugins.Sample.Guestbook;

/// <summary>
/// <c>guestbook.entries@1</c>: the one implementation of the contract, used by every caller —
/// the site through the dispatcher or the plugin's own route, the admin console and AI agents,
/// and other plugins in-process. The runtime constructs it per call with the caller's view of
/// this plugin (<see cref="IPluginContext"/>) and its dependencies from DI, and proxies it:
/// every Safe call is audited and traced without a line here.
/// </summary>
public sealed class Guestbook(IPluginContext context, GuestbookOptions options, IHttpContextAccessor http) : IGuestbook
{
    private readonly EntryStore _store = new(context);

    public async Task<EntryList> ListAsync(ListEntries input, CancellationToken ct)
    {
        var instance = Instance();
        var (entries, total) = await _store.ListAsync(instance.InstanceId, EntryStatus.Approved, Math.Max(input.Page, 1), input.PageSize, ct);
        return new EntryList(entries, total);
    }

    public async Task<SignResult> SignAsync(SignGuestbook input, CancellationToken ct)
    {
        var instance = Instance();
        var name = (input.Name ?? "").Trim();
        var message = (input.Message ?? "").Trim();
        if (message.Length == 0 || message.Length > options.MaxMessageLength)
        {
            throw new ContractValidationException($"Write a message of 1 to {options.MaxMessageLength} characters.");
        }

        // dcms.cache: a counter per address per guestbook, kept for an hour. The platform
        // prefixes the key with tenant and plugin, so it cannot collide with anyone else's.
        var address = http.HttpContext?.Connection.RemoteIpAddress?.ToString() ?? "unknown";
        var count = await context.Contracts.Get<IPluginCache>()
            .IncrementAsync(new CacheIncrement($"sign:{instance.InstanceId:N}:{address}", 1, 3600), ct);
        if (count.Value > options.SignaturesPerHour)
        {
            throw new ContractLimitException("That is enough signatures from here for now; try again in an hour.");
        }

        // Optional integration: when the site has visitor accounts (VisitorAuth), a signed-in
        // visitor signs as themselves. TryGet answers null when nothing provides it.
        Guid? visitorId = null;
        if (context.Contracts.TryGet<IVisitorIdentity>() is { } identity
            && (await identity.GetCurrentAsync(ct)).Visitor is { } visitor)
        {
            visitorId = visitor.Id;
            if (name.Length == 0)
            {
                name = visitor.DisplayName ?? visitor.Email.Split('@')[0];
            }
        }
        if (name.Length is 0 or > 80)
        {
            throw new ContractValidationException("Sign with a name of 1 to 80 characters.");
        }

        // Our own hook: anyone consuming guestbook.entries@1 (this plugin included) may rewrite
        // or refuse the signature before it is stored.
        var outcome = await context.Hooks.RunAsync(new EntrySigning(instance.InstanceId, name, message, visitorId), ct);
        if (outcome.Cancelled)
        {
            throw new ContractValidationException(outcome.Reason ?? "The guestbook did not accept that.");
        }

        var premoderated = Setting(instance, "moderation") != "post";
        var now = DateTimeOffset.UtcNow;
        var entry = new GuestbookEntry(EntryStore.NewId(now), outcome.Value.Name, outcome.Value.Message,
            premoderated ? EntryStatus.Pending : EntryStatus.Approved, now, visitorId);
        await _store.SaveAsync(instance.InstanceId, entry, ct);
        await _store.RememberAsync(instance, ct);

        // dcms.events: other plugins (and this one's NotifyModerators) react off the request.
        await context.PublishAsync(new EntrySigned(instance.InstanceId, entry.Id, entry.Name, premoderated), ct);
        return new SignResult(entry, premoderated);
    }

    public async Task<GuestbookEntry> ApproveAsync(EntryRef input, CancellationToken ct)
    {
        var instance = Instance();
        var entry = await _store.GetAsync(instance.InstanceId, input.EntryId, ct)
                    ?? throw new ContractValidationException("No such entry.");
        if (entry.Status == EntryStatus.Approved)
        {
            return entry;
        }
        var approved = entry with { Status = EntryStatus.Approved };
        await _store.SaveAsync(instance.InstanceId, approved, ct);
        await context.PublishAsync(new EntryApproved(instance.InstanceId, entry.Id), ct);
        return approved;
    }

    public async Task<GuestbookStats> StatsAsync(CancellationToken ct)
    {
        var instance = Instance();
        // Optional integration, typed: another plugin's data without parsing its JSON.
        string? latest = null;
        if (context.Contracts.TryGet<IBlogPosts>() is { } blog)
        {
            latest = (await blog.ListAsync(new ContentPageRequest(PageSize: 1), ct)).Items.FirstOrDefault()?.Data.Title;
        }
        return new GuestbookStats(
            await _store.CountAsync(instance.InstanceId, EntryStatus.Pending, ct),
            await _store.CountAsync(instance.InstanceId, EntryStatus.Approved, ct),
            await _store.CountAsync(instance.InstanceId, EntryStatus.Rejected, ct),
            latest);
    }

    private PluginInstanceContext Instance() =>
        context.Instance ?? throw new ContractValidationException("Name the guestbook: this runs for one instance.");

    /// <summary>A string from an instance's config (the tenant's settings, edited in the admin).</summary>
    internal static string? Setting(PluginInstanceContext instance, string key) =>
        instance.Config.RootElement is { ValueKind: JsonValueKind.Object } root
        && root.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
}
