using System.Text.Json;

namespace Dcms.PluginSdk.Abstractions;

public sealed record ContentItemDto(
    Guid Id,
    Guid PluginInstanceId,
    string ContentType,
    string Slug,
    int VersionNo,
    JsonElement Data,
    DateTimeOffset PublishedAt);

public sealed record PagedResult<T>(IReadOnlyList<T> Items, int Page, int PageSize, long TotalCount);

public sealed record ContentQuery(
    int Page = 1,
    int PageSize = 20,
    string? OrderBy = null,
    bool Descending = false,
    IReadOnlyDictionary<string, string>? Filters = null);

/// <summary>Reference to a published content item in another plugin instance.</summary>
public sealed record ContentRef(Guid InstanceId, string ContentType, Guid ItemId);

public sealed record MediaAssetDto(
    Guid Id,
    MediaCategory Category,
    string FileName,
    string ContentType,
    string Status,
    IReadOnlyDictionary<string, string> VariantUrls);

public sealed record SearchDocument(
    Guid ContentItemId,
    string Title,
    string Body,
    string Url);
