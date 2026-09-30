using System.Text.Json;
using Dcms.PluginSdk.Abstractions;
using Dcms.PluginSdk.Abstractions.Contracts;
using Dcms.PluginSdk.Abstractions.Platform;
using Dcms.Shared.Caching;
using Dcms.Shared.Contracts.Events;
using Dcms.Shared.Contracts.Messaging;
using Dcms.Shared.Messaging;
using Dcms.Shared.Messaging.Email;
using Dcms.Shared.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Dcms.PluginSdk.Runtime.Platform;

// Every class here stamps the tenant and plugin from the caller's IPluginContext onto what it
// touches -- a key prefix, a message field -- and takes neither from input. That is the whole
// isolation story for services that, unlike Postgres, have no RLS of their own.

/// <summary><see cref="IPluginBlobs"/> over the media bucket, under <c>tenants/{tenant}/plugins/{plugin}/</c>.</summary>
public sealed class PluginBlobs(IPluginContext caller, IObjectStorage storage, IOptions<StorageOptions> options) : IPluginBlobs
{
    public const int MaxBytes = 10 * 1024 * 1024;
    private const int MaxListed = 1000;

    private string Bucket => options.Value.MediaBucket;

    // Under the tenant's media prefix so the tenant purge's prefix delete takes these too. Media
    // delivery addresses objects by asset id (a GUID route segment), which "plugins" never is.
    private string Root => $"tenants/{caller.TenantId}/plugins/{caller.PluginId}/";

    public async Task PutAsync(BlobPut input, CancellationToken ct)
    {
        var key = Resolve(input.Key);
        if (input.Content is null || input.Content.Length > MaxBytes)
        {
            throw new ContractValidationException($"Blob must be at most {MaxBytes / 1024 / 1024} MiB.");
        }
        if (string.IsNullOrWhiteSpace(input.ContentType) || input.ContentType.Length > 128)
        {
            throw new ContractValidationException("ContentType is required.");
        }
        using var stream = new MemoryStream(input.Content, writable: false);
        await storage.PutAsync(Bucket, key, stream, input.Content.Length, input.ContentType, ct);
    }

    public async Task<BlobContent?> GetAsync(BlobKey input, CancellationToken ct)
    {
        var key = Resolve(input.Key);
        var info = await storage.StatAsync(Bucket, key, ct);
        if (info is null)
        {
            return null;
        }
        using var buffer = new MemoryStream((int)Math.Min(info.Size, MaxBytes));
        await storage.GetToAsync(Bucket, key, buffer, ct: ct);
        return new BlobContent(input.Key, info.ContentType ?? "application/octet-stream", buffer.ToArray());
    }

    public async Task<DeleteResult> DeleteAsync(BlobKey input, CancellationToken ct)
    {
        var key = Resolve(input.Key);
        if (!await storage.ExistsAsync(Bucket, key, ct))
        {
            return new DeleteResult(false);
        }
        await storage.DeleteAsync(Bucket, key, ct);
        return new DeleteResult(true);
    }

    public async Task<BlobList> ListAsync(BlobPrefix input, CancellationToken ct)
    {
        var prefix = input.Prefix.Length == 0 ? Root : Resolve(input.Prefix);
        var keys = new List<string>();
        await foreach (var key in storage.ListKeysAsync(Bucket, prefix, ct))
        {
            keys.Add(key[Root.Length..]);
            if (keys.Count == MaxListed)
            {
                break;
            }
        }
        return new BlobList(keys);
    }

    /// <summary>The full object key, refusing anything that could step outside the plugin's area.</summary>
    internal string Resolve(string key)
    {
        if (string.IsNullOrWhiteSpace(key) || key.Length > 512 || key.StartsWith('/') || key.Contains('\\')
            || key.Split('/').Any(part => part is "." or "..") || key.Any(char.IsControl))
        {
            throw new ContractValidationException("Blob key must be a relative path without '.', '..' or control characters.");
        }
        return Root + key;
    }
}

/// <summary><see cref="IPluginCache"/> over <see cref="ICacheService"/>, under <c>t:{tenant}:p:{plugin}:</c>.</summary>
public sealed class PluginCache(IPluginContext caller, ICacheService cache) : IPluginCache
{
    private static readonly TimeSpan DefaultTtl = TimeSpan.FromHours(1);
    private static readonly TimeSpan MaxTtl = TimeSpan.FromDays(30);

    public async Task<CacheEntry> GetAsync(CacheKey input, CancellationToken ct) =>
        new(await cache.GetAsync<JsonElement?>(Resolve(input.Key), ct));

    public Task SetAsync(CacheSet input, CancellationToken ct)
    {
        var ttl = input.TtlSeconds is { } s
            ? TimeSpan.FromSeconds(Math.Clamp(s, 1, (int)MaxTtl.TotalSeconds))
            : DefaultTtl;
        return cache.SetAsync<JsonElement?>(Resolve(input.Key), input.Value, ttl, ct);
    }

    public Task RemoveAsync(CacheKey input, CancellationToken ct) => cache.RemoveAsync(Resolve(input.Key), ct);

    public async Task<CacheCounter> IncrementAsync(CacheIncrement input, CancellationToken ct)
    {
        var window = TimeSpan.FromSeconds(Math.Clamp(input.WindowSeconds, 1, (int)MaxTtl.TotalSeconds));
        return new CacheCounter(await cache.IncrementAsync(Resolve(input.Key), input.By, window, ct));
    }

    internal string Resolve(string key)
    {
        if (string.IsNullOrWhiteSpace(key) || key.Length > 200)
        {
            throw new ContractValidationException("Cache key must be 1-200 characters.");
        }
        return $"t:{caller.TenantId}:p:{caller.PluginId}:{key}";
    }
}

/// <summary><see cref="IPluginEmail"/> over the EMAIL work queue, rate-limited per tenant and plugin.</summary>
public sealed class PluginEmail(IPluginContext caller, IEmailQueue queue, ICacheService cache) : IPluginEmail
{
    /// <summary>Recipient-messages per tenant, per plugin, per hour.</summary>
    public const int HourlyLimit = 1000;
    private const int MaxRecipients = 50;

    public async Task<EmailQueued> SendAsync(EmailSend input, CancellationToken ct)
    {
        var to = (input.To ?? []).Select(a => a?.Trim()).Where(a => !string.IsNullOrEmpty(a)).Distinct().ToList();
        if (to.Count is 0 or > MaxRecipients || to.Any(a => !LooksLikeEmail(a!)))
        {
            throw new ContractValidationException($"Between 1 and {MaxRecipients} valid recipient addresses are required.");
        }
        if (string.IsNullOrWhiteSpace(input.Subject) || input.Subject.Length > 256 || input.Subject.Any(char.IsControl))
        {
            throw new ContractValidationException("Subject is required, at most 256 characters, one line.");
        }
        if (string.IsNullOrWhiteSpace(input.HtmlBody) || input.HtmlBody.Length > 512 * 1024)
        {
            throw new ContractValidationException("HtmlBody is required and at most 512 KiB.");
        }
        if (input.ReplyTo is { } replyTo && !LooksLikeEmail(replyTo))
        {
            throw new ContractValidationException("ReplyTo is not an email address.");
        }

        var sent = await cache.IncrementAsync(
            $"t:{caller.TenantId}:p:{caller.PluginId}:_email-sent", to.Count, TimeSpan.FromHours(1), ct);
        if (sent > HourlyLimit)
        {
            throw new ContractLimitException($"Plugin '{caller.PluginId}' may send {HourlyLimit} emails an hour for this workspace.");
        }

        await queue.EnqueueAsync(new EmailMessage(
            to!,
            input.Subject,
            input.HtmlBody,
            input.ReplyTo,
            Purpose: $"plugin:{caller.PluginId}",
            TenantId: caller.TenantId,
            DedupeKey: input.DedupeKey is { } key ? $"plugin:{caller.PluginId}:{key}" : null), ct);
        return new EmailQueued(to.Count);
    }

    private static bool LooksLikeEmail(string address) =>
        address.Length <= 254 && address.IndexOf('@') is > 0 and var at && at < address.Length - 1
        && !address.Any(c => char.IsWhiteSpace(c) || char.IsControl(c));
}

/// <summary>
/// <see cref="IPluginNotifications"/> over <c>notify.raise</c>. The plugin supplies prose (it has
/// no entry in the SPA's locale bundles); the generic <c>plugin_message</c> kind renders it.
/// </summary>
public sealed class PluginNotifications(IPluginContext caller, IEventPublisher events) : IPluginNotifications
{
    public Task RaiseAsync(NotificationRaise input, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(input.Title) || input.Title.Length > 200
            || string.IsNullOrWhiteSpace(input.Body) || input.Body.Length > 1000)
        {
            throw new ContractValidationException("Title (1-200) and Body (1-1000 characters) are required.");
        }
        if (string.IsNullOrWhiteSpace(input.RequiredPermission) || string.IsNullOrWhiteSpace(input.DedupeKey)
            || input.DedupeKey.Length > 200)
        {
            throw new ContractValidationException("RequiredPermission and a DedupeKey of at most 200 characters are required.");
        }
        if (input.LinkPath is { } link && (!link.StartsWith('/') || link.StartsWith("//", StringComparison.Ordinal)))
        {
            throw new ContractValidationException("LinkPath must be an admin path starting with a single '/'.");
        }

        if (input.Kind is { } name && (name.Length > 64 || !PluginRegistry.IsKebabCase(name) || name.Length == 0))
        {
            throw new ContractValidationException("Kind must be kebab-case, at most 64 characters.");
        }

        // A plugin may only claim kinds under its own id, so it can never render as (or be
        // mistaken for) a platform notification such as a failed build.
        // NotificationKinds.PluginMessage in admin-api renders the unlocalised case.
        var kind = input.Kind is { } own ? $"plugin.{caller.PluginId}.{own}" : "plugin.message";
        var slug = kind.Replace('.', '_');
        var values = new Dictionary<string, string>(input.Params ?? new Dictionary<string, string>())
        {
            ["title"] = input.Title,
            ["body"] = input.Body,
            ["plugin"] = caller.PluginId,
        };

        return events.PublishAsync(Subjects.NotifyRaise, new NotificationRaiseRequested(
            EventId: Guid.NewGuid(),
            OccurredAt: DateTimeOffset.UtcNow,
            TenantId: caller.TenantId,
            Kind: kind,
            Severity: input.Severity.ToString(),
            RequiredPermission: input.RequiredPermission,
            TitleKey: $"notifications.kinds.{slug}.title",
            BodyKey: $"notifications.kinds.{slug}.body",
            ParamsJson: JsonSerializer.Serialize(values),
            DedupeKey: $"plugin:{caller.PluginId}:{input.DedupeKey}",
            LinkPath: input.LinkPath,
            ResourceType: input.ResourceType,
            ResourceId: input.ResourceId), ct).AsTask();
    }
}

/// <summary><see cref="IPluginContent"/> over <see cref="PublishedContentReader"/>: published only, tenant only.</summary>
public sealed class PluginContent(IPluginContext caller, PublishedContentReader reader) : IPluginContent
{
    public Task<ContentItemDto?> GetBySlugAsync(ContentLookup input, CancellationToken ct) =>
        reader.GetBySlugAsync(caller.TenantId, InstanceFor(input.InstanceId), input.ContentType, input.Slug, ct);

    public Task<PagedResult<ContentItemDto>> ListAsync(ContentListRequest input, CancellationToken ct) =>
        reader.ListAsync(
            caller.TenantId, InstanceFor(input.InstanceId), input.ContentType,
            Math.Max(input.Page, 1), Math.Clamp(input.PageSize, 1, 100), ct);

    public async Task<ContentItemDto?> ResolveAsync(ContentRef input, CancellationToken ct)
    {
        // A ContentRef carries the item id; the reader is slug-keyed, so list the one id.
        var page = await reader.ListAsync(caller.TenantId, input.InstanceId, input.ContentType, 1, 1, ct, [input.ItemId]);
        return page.Items.FirstOrDefault();
    }

    private Guid InstanceFor(Guid? requested) =>
        requested ?? caller.Instance?.InstanceId
        ?? throw new ContractValidationException("No instance in this context; pass InstanceId.");
}

/// <summary>
/// <see cref="IPluginMedia"/> over <c>media.assets</c>, as the public plane has it: resolving
/// only. The admin plane registers an implementation that can also import. The caller's tenant is an explicit
/// predicate as well as the ambient scope, so a job or event handler reads its own tenant's
/// library exactly as a request does.
/// </summary>
public class PluginMedia(IPluginContext caller, Dcms.Shared.Data.Media.MediaDbContext db) : IPluginMedia
{
    /// <summary>The calling plugin; its tenant is the one every read and write belongs to.</summary>
    protected IPluginContext Caller => caller;

    public virtual Task<MediaImportResult> ImportAsync(MediaImport input, CancellationToken ct) =>
        throw new InvalidOperationException("dcms.media@1 imports only on the admin plane (a job, event handler or admin route).");

    public async Task<MediaAssetDto?> ResolveAsync(MediaLookup input, CancellationToken ct)
    {
        using var rls = Dcms.Shared.Data.Rls.RlsScope.Tenant(caller.TenantId);
        var asset = await db.Assets.AsNoTracking()
            .Include(a => a.Variants)
            .FirstOrDefaultAsync(a => a.TenantId == caller.TenantId && a.Id == input.AssetId, ct);
        if (asset is null)
        {
            return null;
        }

        var urls = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["original"] = $"/api/media/{asset.Id}/original",
        };
        foreach (var variant in asset.Variants)
        {
            urls[variant.Kind] = $"/api/media/{asset.Id}/{variant.Kind}";
        }
        return new MediaAssetDto(
            asset.Id, (Dcms.PluginSdk.Abstractions.MediaCategory)(int)asset.Category, asset.FileName, asset.ContentType, asset.Status.ToString(), urls);
    }
}
