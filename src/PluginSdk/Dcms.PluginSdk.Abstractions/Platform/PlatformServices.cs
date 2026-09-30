using System.Text.Json;
using Dcms.PluginSdk.Abstractions.Contracts;

namespace Dcms.PluginSdk.Abstractions.Platform;

// ---------------------------------------------------------------------------------------------
// dcms.blobs@1
// ---------------------------------------------------------------------------------------------

/// <summary>A key relative to the calling plugin's own blob area. No leading '/', no '..'.</summary>
public sealed record BlobKey(string Key);

public sealed record BlobPut(string Key, string ContentType, byte[] Content);

public sealed record BlobContent(string Key, string ContentType, byte[] Content);

public sealed record BlobPrefix(string Prefix = "");

public sealed record BlobList(IReadOnlyList<string> Keys);

/// <summary>
/// Private files for the calling plugin: object storage under the tenant's own prefix, so a
/// tenant purge takes them with everything else. Whole-object and capped (10 MiB) — these are
/// exports, caches and imports, not a second media library; user-visible files belong in media.
/// </summary>
[DcmsContract("dcms.blobs", 1, Description = "Private file storage for the calling plugin.")]
public interface IPluginBlobs
{
    [Operation(OpRisk.Safe)]
    Task PutAsync(BlobPut input, CancellationToken ct);

    [Operation(OpRisk.Read)]
    Task<BlobContent?> GetAsync(BlobKey input, CancellationToken ct);

    [Operation(OpRisk.Safe)]
    Task<DeleteResult> DeleteAsync(BlobKey input, CancellationToken ct);

    [Operation(OpRisk.Read)]
    Task<BlobList> ListAsync(BlobPrefix input, CancellationToken ct);
}

// ---------------------------------------------------------------------------------------------
// dcms.cache@1
// ---------------------------------------------------------------------------------------------

public sealed record CacheKey(string Key);

/// <param name="TtlSeconds">Defaults to one hour; capped at 30 days.</param>
public sealed record CacheSet(string Key, JsonElement Value, int? TtlSeconds = null);

public sealed record CacheEntry(JsonElement? Value);

/// <summary>A counter over a fixed window, e.g. a rate limit: <c>Increment("sends", 1, 3600)</c>.</summary>
public sealed record CacheIncrement(string Key, long By = 1, int WindowSeconds = 3600);

public sealed record CacheCounter(long Value);

/// <summary>Shared cache (Redis) under the calling plugin's own tenant-scoped key prefix.</summary>
[DcmsContract("dcms.cache", 1, Description = "Tenant- and plugin-scoped cache and counters.")]
public interface IPluginCache
{
    [Operation(OpRisk.Read)]
    Task<CacheEntry> GetAsync(CacheKey input, CancellationToken ct);

    [Operation(OpRisk.Safe)]
    Task SetAsync(CacheSet input, CancellationToken ct);

    [Operation(OpRisk.Safe)]
    Task RemoveAsync(CacheKey input, CancellationToken ct);

    [Operation(OpRisk.Safe)]
    Task<CacheCounter> IncrementAsync(CacheIncrement input, CancellationToken ct);
}

// ---------------------------------------------------------------------------------------------
// dcms.email@1
// ---------------------------------------------------------------------------------------------

/// <param name="DedupeKey">Natural id of the occurrence (a submission id); a redelivery with the same key sends once.</param>
public sealed record EmailSend(
    IReadOnlyList<string> To,
    string Subject,
    string HtmlBody,
    string? ReplyTo = null,
    string? DedupeKey = null);

public sealed record EmailQueued(int Recipients);

/// <summary>
/// Transactional email through the platform queue (email-worker owns SMTP and retries). The
/// plugin renders the message; the platform stamps tenant and purpose and rate-limits per
/// tenant and plugin.
/// </summary>
[DcmsContract("dcms.email", 1, Description = "Queue transactional email.")]
public interface IPluginEmail
{
    /// <exception cref="ContractLimitException">The tenant's hourly allowance for this plugin is spent.</exception>
    [Operation(OpRisk.Safe)]
    Task<EmailQueued> SendAsync(EmailSend input, CancellationToken ct);
}

// ---------------------------------------------------------------------------------------------
// dcms.notifications@1
// ---------------------------------------------------------------------------------------------

public enum NotificationSeverity
{
    Info,
    Warning,
    Error,
}

/// <param name="RequiredPermission">
/// Who hears about it: members holding this key. Use the permission that already gates what
/// the notification is about, so it can never reveal something its recipient could not open.
/// </param>
/// <param name="DedupeKey">Identifies the underlying occurrence, not this call; namespaced per plugin by the platform.</param>
/// <param name="LinkPath">Admin SPA path the notification opens, e.g. <c>/forms</c>.</param>
/// <param name="Kind">
/// Optional, for a notification the admin SPA has translations for: the kind becomes
/// <c>plugin.{pluginId}.{Kind}</c> and renders through its own locale keys, with
/// <see cref="Params"/> (plus <c>title</c> and <c>body</c>) as interpolation values. Without it
/// the notification renders <see cref="Title"/> and <see cref="Body"/> as given.
/// </param>
/// <param name="ResourceType">What the notification is about, for the SPA's live refresh.</param>
public sealed record NotificationRaise(
    string Title,
    string Body,
    string RequiredPermission,
    string DedupeKey,
    NotificationSeverity Severity = NotificationSeverity.Info,
    string? LinkPath = null,
    string? Kind = null,
    IReadOnlyDictionary<string, string>? Params = null,
    string? ResourceType = null,
    Guid? ResourceId = null);

/// <summary>In-app notifications (the admin bell) for the tenant's members.</summary>
[DcmsContract("dcms.notifications", 1, Description = "Raise an in-app notification for tenant members.")]
public interface IPluginNotifications
{
    [Operation(OpRisk.Safe)]
    Task RaiseAsync(NotificationRaise input, CancellationToken ct);
}

// ---------------------------------------------------------------------------------------------
// dcms.media@1
// ---------------------------------------------------------------------------------------------

public sealed record MediaLookup(Guid AssetId);

/// <param name="Url">An https URL to fetch.</param>
/// <param name="FileName">The name the asset is filed under.</param>
public sealed record MediaImport(Uri Url, string FileName, Guid? FolderId = null);

/// <summary>The new asset, or why the file was refused (too large, unrecognised type, unreachable).</summary>
public sealed record MediaImportResult(Guid? AssetId, string? Error);

/// <summary>
/// The tenant's media library: a stored asset id (a MediaRef field, a logo in
/// config) to its servable variant URLs — <c>original</c>, and the processed ladder once ready.
/// </summary>
[DcmsContract("dcms.media", 1, Description = "Resolve the tenant's media assets to servable URLs, and import files into the library.")]
public interface IPluginMedia
{
    /// <summary>Null when the asset does not exist in this tenant.</summary>
    [Operation(OpRisk.Read)]
    Task<MediaAssetDto?> ResolveAsync(MediaLookup input, CancellationToken ct);

    /// <summary>
    /// Downloads a file into the tenant's library through the same pipeline as an upload —
    /// type sniffing, sanitising, the size limit, the processing queue. Admin plane only (jobs,
    /// event handlers, admin routes): the public plane refuses it.
    /// </summary>
    [Operation(OpRisk.Safe)]
    Task<MediaImportResult> ImportAsync(MediaImport input, CancellationToken ct);
}

// ---------------------------------------------------------------------------------------------
// dcms.content@1
// ---------------------------------------------------------------------------------------------

/// <param name="InstanceId">Any instance in the tenant; defaults to the caller's own.</param>
public sealed record ContentLookup(string ContentType, string Slug, Guid? InstanceId = null);

public sealed record ContentListRequest(string ContentType, int Page = 1, int PageSize = 20, Guid? InstanceId = null);

/// <summary>
/// Published content of any plugin instance in the tenant — the same data the public delivery
/// API serves, through the same cache. Drafts are never visible here.
/// </summary>
[DcmsContract("dcms.content", 1, Description = "Read published content and resolve content references.")]
public interface IPluginContent
{
    [Operation(OpRisk.Read)]
    Task<ContentItemDto?> GetBySlugAsync(ContentLookup input, CancellationToken ct);

    [Operation(OpRisk.Read)]
    Task<PagedResult<ContentItemDto>> ListAsync(ContentListRequest input, CancellationToken ct);

    [Operation(OpRisk.Read)]
    Task<ContentItemDto?> ResolveAsync(ContentRef input, CancellationToken ct);
}

// ---------------------------------------------------------------------------------------------
// dcms.search@1
// ---------------------------------------------------------------------------------------------

/// <param name="Limit">At most 50.</param>
/// <param name="BodyChars">How much of each document's text to return; 0 for none, at most 2000.</param>
/// <param name="IncludeTotal">Also count every match (one more query).</param>
public sealed record SearchRequest(string Query, int Limit = 10, int BodyChars = 0, bool IncludeTotal = false);

public sealed record SearchHit(string Title, string Url, string ContentType, string? Body);

public sealed record SearchResults(IReadOnlyList<SearchHit> Items, long? Total);

/// <summary>
/// Full-text search over the tenant's published, searchable content. The index is maintained
/// by the platform for every tenant (whether or not the Search plugin is installed), which is
/// why this is a platform contract: the chatbot grounds its answers in it, and the Search
/// plugin serves it to the public site.
/// </summary>
[DcmsContract("dcms.search", 1, Description = "Full-text search over the tenant's published content.")]
public interface IPluginSearch
{
    // Not Site: platform contracts are not reachable from the public plane; sites search
    // through the Search plugin's own route.
    [Operation(OpRisk.Read, Expose = OpExposure.Ai,
        Description = "Published content matching a query, best match first.")]
    Task<SearchResults> SearchAsync(SearchRequest input, CancellationToken ct);
}

// ---------------------------------------------------------------------------------------------
// dcms.ai@1
// ---------------------------------------------------------------------------------------------

/// <param name="MaxTokens">At most 4000.</param>
public sealed record AiCompletionRequest(string Prompt, string? System = null, int MaxTokens = 800);

/// <summary>Null <see cref="Text"/> when the tenant has no AI provider configured or the call failed.</summary>
public sealed record AiCompletion(string? Text);

/// <summary>
/// A text completion from the tenant's own AI provider, through ai-gateway: the tenant's
/// credentials, quota and audit, exactly as for the site chatbot. The plugin never sees a key.
/// </summary>
[DcmsContract("dcms.ai", 1, Description = "Text completions from the tenant's AI provider, within its quota.")]
public interface IPluginAi
{
    /// <exception cref="ContractLimitException">The tenant's AI quota is spent.</exception>
    [Operation(OpRisk.Safe, Description = "Complete a prompt with the tenant's AI provider.")]
    Task<AiCompletion> CompleteAsync(AiCompletionRequest input, CancellationToken ct);
}
