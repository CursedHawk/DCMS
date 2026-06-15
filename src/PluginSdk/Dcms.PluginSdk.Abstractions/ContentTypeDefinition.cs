namespace Dcms.PluginSdk.Abstractions;

public sealed record ContentTypeDefinition(
    string Name,                        // e.g. "article", "galleryImage"
    IReadOnlyList<ContentFieldDefinition> Fields,
    bool Searchable,                    // contributes to sitewide search
    string? SlugField);

public sealed record ContentFieldDefinition(
    string Name,
    ContentFieldType Type,
    bool Required,
    string? Description,                // flows into OpenAPI schema descriptions
    ContentReferenceTarget? Reference = null);

public enum ContentFieldType
{
    Text,
    RichText,
    Markdown,
    Number,
    Boolean,
    DateTime,
    Json,
    MediaRef,
    ContentRef,
    Tags,
}

public enum MediaCategory
{
    Image,
    Video,
    Audio,
    File,
}

/// <summary>
/// Target of a MediaRef/ContentRef field — the inter-plugin reference contract
/// (e.g. an Articles "gallery" field referencing image-gallery's "gallery" type).
/// </summary>
public sealed record ContentReferenceTarget(
    string? TargetPluginId,
    string? ContentType,
    MediaCategory? MediaCategory);
