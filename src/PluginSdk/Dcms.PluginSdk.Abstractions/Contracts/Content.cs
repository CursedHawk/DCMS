using System.Text.Json;
using System.Text.Json.Serialization;
using Dcms.PluginSdk.Abstractions.Platform;

namespace Dcms.PluginSdk.Abstractions.Contracts;

// Typed access to a plugin's published content, so a content plugin's .Api can offer
// `IBlogPosts.ListAsync → ContentPage<BlogPost>` instead of every consumer parsing
// ContentItemDto.Data by hand. See docs/plugins.md, "Content plugins".

/// <summary>A page of published items, newest first.</summary>
public sealed record ContentPageRequest(int Page = 1, int PageSize = 20);

public sealed record ContentSlugRequest(string Slug);

/// <summary>One published item: the platform envelope plus the plugin's typed fields.</summary>
public sealed record ContentEntry<T>(Guid Id, Guid InstanceId, string Slug, DateTimeOffset PublishedAt, T Data)
    where T : notnull;

public sealed record ContentPage<T>(IReadOnlyList<ContentEntry<T>> Items, int Page, int PageSize, long TotalCount)
    where T : notnull;

/// <summary>
/// Base for a content type's lifecycle events. A content type names its events
/// (<see cref="ContentTypeDefinition.Published"/>, <see cref="ContentTypeDefinition.Unpublished"/>)
/// and the platform raises them for every publish, however it happened — the editor, a schedule,
/// an import. The event records must keep this positional shape:
/// <code>[ContractEvent("blog.post.published")]
/// public sealed record PostPublished(Guid InstanceId, Guid ItemId, string ContentType, string Slug)
///     : ContentChanged(InstanceId, ItemId, ContentType, Slug);</code>
/// </summary>
public abstract record ContentChanged(Guid InstanceId, Guid ItemId, string ContentType, string Slug) : IPluginEvent;

/// <summary>
/// Implements a typed read contract over one of the provider's own content types, through
/// <c>dcms.content@1</c> (which the provider must consume). The instance served is the one the
/// contract was resolved for.
/// </summary>
public abstract class PublishedContentSource<T>(IPluginContext context, string contentType)
    where T : notnull
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        // Editors leave a cleared reference as "" rather than removing it.
        Converters = { new LenientGuidConverter() },
        NumberHandling = JsonNumberHandling.AllowReadingFromString,
    };

    protected IPluginContext Context => context;

    protected async Task<ContentPage<T>> ListAsync(ContentPageRequest input, CancellationToken ct)
    {
        var page = await Content.ListAsync(
            new ContentListRequest(contentType, Math.Max(input.Page, 1), Math.Clamp(input.PageSize, 1, 100), InstanceId), ct);
        return new ContentPage<T>(page.Items.Select(Map).ToList(), page.Page, page.PageSize, page.TotalCount);
    }

    protected async Task<ContentEntry<T>?> GetAsync(ContentSlugRequest input, CancellationToken ct) =>
        await Content.GetBySlugAsync(new ContentLookup(contentType, input.Slug, InstanceId), ct) is { } item ? Map(item) : null;

    /// <summary>Maps one item's data; override to post-process.</summary>
    protected virtual ContentEntry<T> Map(ContentItemDto item) =>
        new(item.Id, item.PluginInstanceId, item.Slug, item.PublishedAt, item.Data.Deserialize<T>(Options)!);

    private IPluginContent Content => context.Contracts.Get<IPluginContent>();

    private Guid InstanceId => context.Instance?.InstanceId
        ?? throw new ContractValidationException("Content contracts are served for one instance.");

    private sealed class LenientGuidConverter : JsonConverter<Guid?>
    {
        public override Guid? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
            reader.TokenType == JsonTokenType.String && Guid.TryParse(reader.GetString(), out var id) ? id : null;

        public override void Write(Utf8JsonWriter writer, Guid? value, JsonSerializerOptions options)
        {
            if (value is { } id)
            {
                writer.WriteStringValue(id);
            }
            else
            {
                writer.WriteNullValue();
            }
        }
    }
}
