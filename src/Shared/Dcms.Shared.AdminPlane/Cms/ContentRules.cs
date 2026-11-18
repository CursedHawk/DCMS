using System.Text.Json;
using System.Text.Json.Nodes;
using Dcms.PluginSdk.Abstractions;
using Dcms.PluginSdk.Runtime;
using Dcms.Shared.Security;

namespace Dcms.AdminApi.Cms;

/// <summary>What a content item may hold, shared by the content endpoints and environment deployments (ContentConfigKind).</summary>
public static class ContentRules
{
    public static bool PluginDeclaresType(IPluginCatalog catalog, string pluginId, string contentType)
        => catalog.Find(pluginId)?.ContentTypes.Any(t => t.Name == contentType) ?? false;

    /// <summary>
    /// Rewrites each declared rich-text string field of <paramref name="data"/> through
    /// <see cref="HtmlContentSanitizer"/>. Returns the original element when the content type is
    /// unknown, declares no rich-text field, or nothing needed changing — so an unchanged save
    /// still round-trips byte-for-byte.
    /// </summary>
    public static JsonElement? SanitizeRichText(
        JsonElement? data, IPluginCatalog catalog, string pluginId, string contentType)
    {
        if (data is not { ValueKind: JsonValueKind.Object } obj) return data;
        var def = catalog.Find(pluginId)?.ContentTypes.FirstOrDefault(t => t.Name == contentType);
        if (def is null) return data;
        var richFields = def.Fields
            .Where(f => f.Type == ContentFieldType.RichText)
            .Select(f => f.Name)
            .ToHashSet(StringComparer.Ordinal);
        if (richFields.Count == 0) return data;

        var node = JsonNode.Parse(obj.GetRawText())!.AsObject();
        var changed = false;
        foreach (var name in richFields)
        {
            if (node.TryGetPropertyValue(name, out var value)
                && value is JsonValue jv
                && jv.TryGetValue<string>(out var html)
                && html is not null)
            {
                var clean = HtmlContentSanitizer.Sanitize(html);
                if (!string.Equals(clean, html, StringComparison.Ordinal))
                {
                    node[name] = clean;
                    changed = true;
                }
            }
        }
        return changed ? JsonSerializer.SerializeToElement(node) : data;
    }
}
