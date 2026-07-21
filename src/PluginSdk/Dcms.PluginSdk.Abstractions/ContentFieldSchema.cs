using System.Text.Json.Nodes;

namespace Dcms.PluginSdk.Abstractions;

/// <summary>
/// Projects a content type's <see cref="ContentFieldDefinition"/> list into a typed
/// JSON Schema object, so the emitted OpenAPI describes each field's real type (not a
/// generic <c>object</c>). Custom <c>x-dcms-*</c> extensions carry the extra intent
/// (media category, cross-plugin reference target, rich-text vs markdown) that the
/// generated TypeScript client turns into branded helper types.
/// </summary>
public static class ContentFieldSchema
{
    /// <summary>Builds the <c>data</c> object schema for a content type's fields.</summary>
    public static JsonObject BuildDataObject(ContentTypeDefinition? definition)
    {
        if (definition is null)
        {
            // Unknown content type (shouldn't happen for declared types) — stay permissive.
            return new JsonObject { ["type"] = "object" };
        }

        var properties = new JsonObject();
        var required = new JsonArray();
        foreach (var field in definition.Fields)
        {
            properties[field.Name] = ForField(field);
            if (field.Required)
            {
                required.Add(field.Name);
            }
        }

        var schema = new JsonObject
        {
            ["type"] = "object",
            ["properties"] = properties,
        };
        if (required.Count > 0)
        {
            schema["required"] = required;
        }
        return schema;
    }

    /// <summary>Maps a single field definition to its JSON Schema fragment.</summary>
    public static JsonObject ForField(ContentFieldDefinition field)
    {
        var schema = field.Type switch
        {
            ContentFieldType.Text => new JsonObject { ["type"] = "string" },
            ContentFieldType.RichText => new JsonObject { ["type"] = "string", ["x-dcms-format"] = "richtext" },
            ContentFieldType.Markdown => new JsonObject { ["type"] = "string", ["x-dcms-format"] = "markdown" },
            ContentFieldType.Number => new JsonObject { ["type"] = "number" },
            ContentFieldType.Boolean => new JsonObject { ["type"] = "boolean" },
            ContentFieldType.DateTime => new JsonObject { ["type"] = "string", ["format"] = "date-time" },
            ContentFieldType.Json => new JsonObject { ["type"] = "object" },
            ContentFieldType.MediaRef => MediaRef(field.Reference),
            ContentFieldType.ContentRef => ContentRef(field.Reference),
            ContentFieldType.Tags => new JsonObject
            {
                ["type"] = "array",
                ["items"] = new JsonObject { ["type"] = "string" },
            },
            _ => new JsonObject { ["type"] = "string" },
        };

        if (!string.IsNullOrWhiteSpace(field.Description))
        {
            schema["description"] = field.Description;
        }
        return schema;
    }

    // A media reference is the asset's GUID, resolved to a URL at
    // /api/media/{assetId}/{variant}. The category tells the client which variants exist.
    private static JsonObject MediaRef(ContentReferenceTarget? target)
    {
        var schema = new JsonObject { ["type"] = "string", ["format"] = "uuid" };
        if (target?.MediaCategory is { } category)
        {
            schema["x-dcms-media-category"] = category.ToString().ToLowerInvariant();
        }
        return schema;
    }

    // A content reference is the referenced item's GUID; the target names the plugin
    // and content type it points at so the client can fetch it.
    private static JsonObject ContentRef(ContentReferenceTarget? target)
    {
        var schema = new JsonObject { ["type"] = "string", ["format"] = "uuid" };
        if (target is not null && (target.TargetPluginId is not null || target.ContentType is not null))
        {
            var reference = new JsonObject();
            if (target.TargetPluginId is not null)
            {
                reference["pluginId"] = target.TargetPluginId;
            }
            if (target.ContentType is not null)
            {
                reference["contentType"] = target.ContentType;
            }
            schema["x-dcms-content-ref"] = reference;
        }
        return schema;
    }
}
