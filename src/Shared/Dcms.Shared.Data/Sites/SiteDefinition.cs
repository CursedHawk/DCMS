using System.Text.Json.Serialization;

namespace Dcms.Shared.Data.Sites;

/// <summary>
/// C# mirror of the editor-core component-tree schema, used by the site-builder
/// prerenderer. Kept intentionally permissive (props/bindings are open maps) so
/// it tolerates editor/AI evolution without breaking the build.
/// </summary>
public sealed class SiteDefinition
{
    public int Version { get; set; } = 1;
    public ThemeTokens Theme { get; set; } = new();
    public List<SitePage> Pages { get; set; } = [];
    public List<NavItem> Nav { get; set; } = [];
}

public sealed class ThemeTokens
{
    public Dictionary<string, string> Colors { get; set; } = [];
    public Dictionary<string, string> Fonts { get; set; } = [];
    public string? Radius { get; set; }
}

public sealed class SitePage
{
    public string Id { get; set; } = string.Empty;
    public string Path { get; set; } = "/";
    public string Title { get; set; } = string.Empty;
    public SeoMeta Seo { get; set; } = new();
    public ComponentNode? Root { get; set; }
}

public sealed class SeoMeta
{
    public string Title { get; set; } = string.Empty;
    public string? Description { get; set; }
    public string? OgImage { get; set; }
}

public sealed class NavItem
{
    public string Label { get; set; } = string.Empty;
    public string Path { get; set; } = "/";
}

public sealed class ComponentNode
{
    public string Id { get; set; } = string.Empty;
    public string Type { get; set; } = string.Empty;
    public Dictionary<string, JsonElementValue> Props { get; set; } = [];
    public List<DataBinding> Bindings { get; set; } = [];
    public List<ComponentNode> Children { get; set; } = [];
}

public sealed class DataBinding
{
    public string PropPath { get; set; } = string.Empty;
    public BindingSource Source { get; set; } = new();
}

public sealed class BindingSource
{
    public string InstanceSlug { get; set; } = string.Empty;
    public Dictionary<string, JsonElementValue> Query { get; set; } = [];
}

/// <summary>Wrapper that preserves arbitrary JSON prop values.</summary>
[JsonConverter(typeof(JsonElementValueConverter))]
public readonly struct JsonElementValue(System.Text.Json.JsonElement element)
{
    public System.Text.Json.JsonElement Element { get; } = element;
    public string? AsString() => Element.ValueKind == System.Text.Json.JsonValueKind.String ? Element.GetString() : Element.ToString();
}

public sealed class JsonElementValueConverter : JsonConverter<JsonElementValue>
{
    public override JsonElementValue Read(ref System.Text.Json.Utf8JsonReader reader, Type typeToConvert, System.Text.Json.JsonSerializerOptions options)
        => new(System.Text.Json.JsonElement.ParseValue(ref reader));

    public override void Write(System.Text.Json.Utf8JsonWriter writer, JsonElementValue value, System.Text.Json.JsonSerializerOptions options)
        => value.Element.WriteTo(writer);
}
