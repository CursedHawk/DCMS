namespace Dcms.PluginSdk.Abstractions;

public sealed record ContentTypeDefinition(
    string Name,                        // e.g. "article", "galleryImage"
    IReadOnlyList<ContentFieldDefinition> Fields,
    bool Searchable,                    // contributes to sitewide search
    string? SlugField,
    CustomFieldsDefinition? CustomFields = null);

/// <summary>
/// Declares that a content type carries fields the tenant admin defines, not the
/// plugin: the definitions live under <see cref="ConfigKey"/> in the instance
/// config, and an item's values under <see cref="ValuesField"/> in its data.
///
/// Values are nested rather than spread over the item so an admin-defined key can
/// never shadow one of the plugin's own fields, and so dropping a definition
/// leaves the authored value recoverable instead of silently orphaned.
/// </summary>
public sealed record CustomFieldsDefinition(string ValuesField, string ConfigKey);

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
