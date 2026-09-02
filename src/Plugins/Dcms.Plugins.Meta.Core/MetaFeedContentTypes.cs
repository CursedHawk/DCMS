using Dcms.PluginSdk.Abstractions;

namespace Dcms.Plugins.Meta.Core;

/// <summary>
/// The shape a mirrored Meta post takes once it is a DCMS content item.
///
/// <para>Modelling these as ordinary plugin content types is what makes the whole feature
/// small: the delivery API, the Redis cache, the per-tenant OpenAPI document, the GrapesJS
/// palette and the search index are all driven off content-type declarations, so a synced
/// Instagram post gets every one of them without a line of code in any of those places.</para>
///
/// <para>The fields are shared between Instagram and Facebook deliberately. A site author
/// dropping a feed on a page cares about a caption, a picture, a link and a date; making the
/// two providers differ in field names would mean two sets of builder bindings and two
/// template shapes for what is, to the page, the same thing.</para>
/// </summary>
public static class MetaFeedContentTypes
{
    /// <summary>
    /// The Meta media id, used as the item slug. Stable, unique within an account, and already
    /// URL-safe — and being the identifier Meta itself uses is what makes re-sync an upsert
    /// rather than a diff.
    /// </summary>
    public const string SlugField = "externalId";

    public static ContentTypeDefinition Build(string name, string subject) => new(
        Name: name,
        Fields:
        [
            new ContentFieldDefinition(SlugField, ContentFieldType.Text, Required: true,
                $"The {subject}'s id on Meta. Also the item slug."),
            new ContentFieldDefinition("permalink", ContentFieldType.Text, Required: false,
                $"Public URL of the {subject} on Meta."),
            new ContentFieldDefinition("caption", ContentFieldType.Text, Required: false,
                $"The {subject}'s caption or message text."),
            new ContentFieldDefinition("media", ContentFieldType.MediaRef, Required: false,
                "Mirrored image or video, served from DCMS media.",
                new ContentReferenceTarget(null, null, MediaCategory.Image)),
            new ContentFieldDefinition("thumbnail", ContentFieldType.MediaRef, Required: false,
                "Mirrored poster frame, for video and reels.",
                new ContentReferenceTarget(null, null, MediaCategory.Image)),
            new ContentFieldDefinition("mediaType", ContentFieldType.Text, Required: false,
                "IMAGE, VIDEO or CAROUSEL_ALBUM, as reported by Meta."),
            new ContentFieldDefinition("mediaUrl", ContentFieldType.Text, Required: false,
                "Meta CDN URL. Only populated when mirroring is off for this media — Meta's "
                + "CDN links are signed and expire, so a site should prefer the mirrored asset."),
            new ContentFieldDefinition("postedAt", ContentFieldType.DateTime, Required: false,
                $"When the {subject} was published on Meta."),
            new ContentFieldDefinition("username", ContentFieldType.Text, Required: false,
                "The account that posted it."),
            new ContentFieldDefinition("children", ContentFieldType.Json, Required: false,
                "Carousel members, each with its own mirrored asset."),
        ],
        Searchable: true,
        SlugField: SlugField);
}
