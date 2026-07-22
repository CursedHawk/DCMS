using System.Text.Json;
using System.Text.Json.Nodes;
using Dcms.PluginSdk.Abstractions;
using Microsoft.Extensions.DependencyInjection;

namespace Dcms.Plugins.Forms;

/// <summary>
/// Visitor-submitted forms (contact, booking, sign-up). Forms are declared in the
/// instance config rather than as content types, because a submission is a write
/// from the public site, not published content.
///
/// The endpoint itself lives in content-api (FormSubmissionEndpoints) — the plugin
/// SDK does not mount custom plugin routes — so this plugin contributes the
/// manifest and a per-form OpenAPI fragment describing the POST body.
/// </summary>
public sealed class FormsPlugin : IPlugin
{
    public const string PluginId = "forms";

    private const string ConfigSchema = """
        {
          "type": "object",
          "properties": {
            "forms": {
              "type": "array",
              "title": "Forms",
              "items": {
                "type": "object",
                "properties": {
                  "name": {
                    "type": "string",
                    "title": "Form name",
                    "description": "URL-safe identifier; the submit path is /api/{instanceSlug}/forms/{name}.",
                    "pattern": "^[a-z0-9][a-z0-9-]*$"
                  },
                  "title": { "type": "string", "title": "Display title" },
                  "successMessage": { "type": "string", "title": "Message returned on success" },
                  "fields": {
                    "type": "array",
                    "title": "Fields",
                    "items": {
                      "type": "object",
                      "properties": {
                        "name": { "type": "string", "pattern": "^[a-zA-Z][a-zA-Z0-9_]*$" },
                        "label": { "type": "string" },
                        "type": { "type": "string", "enum": ["text", "email", "date", "number", "textarea", "checkbox"], "default": "text" },
                        "required": { "type": "boolean", "default": false },
                        "maxLength": { "type": "integer", "minimum": 1, "maximum": 10000, "default": 2000 }
                      },
                      "required": ["name"],
                      "additionalProperties": false
                    }
                  }
                },
                "required": ["name", "fields"],
                "additionalProperties": false
              }
            }
          },
          "additionalProperties": false
        }
        """;

    public PluginManifest Manifest { get; } = PluginManifest.Create(
        id: PluginId,
        name: "Forms",
        description: "Visitor-submitted forms (contact, booking, sign-up) with submissions stored for review in the admin.",
        allowMultipleInstances: true,
        configJsonSchema: ConfigSchema,
        permissions:
        [
            new PermissionDefinition("read", "View form submissions"),
            new PermissionDefinition("write", "Configure forms"),
        ]);

    public void ConfigureServices(IServiceCollection services) { }

    // No content types and no custom routes: submissions are served by content-api.
    public void MapEndpoints(IPluginEndpointBuilder endpoints) { }

    public OpenApiFragment BuildOpenApiFragment(PluginInstanceContext instance)
    {
        var paths = new List<OpenApiPathFragment>();
        var schemas = new Dictionary<string, JsonNode>();

        foreach (var form in ReadForms(instance.Config))
        {
            var bodySchemaName = $"{instance.Slug}_{form.Name}_submission";
            schemas[bodySchemaName] = BuildBodySchema(form);

            paths.Add(new OpenApiPathFragment(
                RelativePath: $"/forms/{form.Name}",
                Method: "post",
                OperationId: $"{instance.Slug}_submit_{form.Name}",
                Summary: $"Submit the {form.Title ?? form.Name} form",
                Description: $"{instance.Description}\n\nRecords a visitor submission of the "
                             + $"\"{form.Title ?? form.Name}\" form.\n\nPlugin: {Manifest.Name} v{Manifest.Version}",
                ResponseSchema: SubmissionResultSchema(),
                RequestBodySchema: Ref(bodySchemaName),
                SuccessStatus: "202"));
        }

        schemas[$"{instance.Slug}_submission_result"] = SubmissionResultObject();

        return new OpenApiFragment(
            TagName: instance.Name,
            TagDescription: $"{instance.Description}\n\n{Manifest.Description}",
            Paths: paths,
            Schemas: schemas);
    }

    /// <summary>
    /// The forms declared on an instance. Shared with content-api so the endpoint
    /// validates against exactly what this manifest documents.
    /// </summary>
    public static IReadOnlyList<FormDefinition> ReadForms(JsonDocument config)
    {
        if (config.RootElement.ValueKind != JsonValueKind.Object ||
            !config.RootElement.TryGetProperty("forms", out var forms) ||
            forms.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var result = new List<FormDefinition>();
        foreach (var form in forms.EnumerateArray())
        {
            if (form.ValueKind != JsonValueKind.Object ||
                !form.TryGetProperty("name", out var nameProp) ||
                nameProp.GetString() is not { Length: > 0 } name)
            {
                continue;
            }

            var fields = new List<FormFieldDefinition>();
            if (form.TryGetProperty("fields", out var fieldArray) && fieldArray.ValueKind == JsonValueKind.Array)
            {
                foreach (var field in fieldArray.EnumerateArray())
                {
                    if (field.ValueKind != JsonValueKind.Object ||
                        !field.TryGetProperty("name", out var fieldName) ||
                        fieldName.GetString() is not { Length: > 0 } fname)
                    {
                        continue;
                    }
                    fields.Add(new FormFieldDefinition(
                        fname,
                        GetString(field, "label"),
                        GetString(field, "type") ?? "text",
                        field.TryGetProperty("required", out var req) && req.ValueKind == JsonValueKind.True,
                        field.TryGetProperty("maxLength", out var max) && max.TryGetInt32(out var maxLen) ? maxLen : 2000));
                }
            }

            result.Add(new FormDefinition(name, GetString(form, "title"), GetString(form, "successMessage"), fields));
        }
        return result;
    }

    private static string? GetString(JsonElement element, string property)
        => element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static JsonObject BuildBodySchema(FormDefinition form)
    {
        var properties = new JsonObject();
        var required = new JsonArray();

        foreach (var field in form.Fields)
        {
            properties[field.Name] = new JsonObject
            {
                ["type"] = field.Type == "checkbox" ? "boolean" : field.Type == "number" ? "number" : "string",
                ["description"] = field.Label ?? field.Name,
                ["maxLength"] = field.Type is "checkbox" or "number" ? null : field.MaxLength,
                ["format"] = field.Type switch
                {
                    "email" => "email",
                    "date" => "date",
                    _ => null,
                },
            };
            if (field.Required)
            {
                required.Add(field.Name);
            }
        }

        var schema = new JsonObject { ["type"] = "object", ["properties"] = properties };
        if (required.Count > 0)
        {
            schema["required"] = required;
        }
        return schema;
    }

    private static JsonObject SubmissionResultObject() => new()
    {
        ["type"] = "object",
        ["properties"] = new JsonObject
        {
            ["submissionId"] = new JsonObject { ["type"] = "string", ["format"] = "uuid" },
            ["message"] = new JsonObject { ["type"] = "string" },
        },
        ["required"] = new JsonArray { "submissionId", "message" },
    };

    private static JsonObject SubmissionResultSchema() => SubmissionResultObject();

    private static JsonObject Ref(string schemaName) => new() { ["$ref"] = $"#/components/schemas/{schemaName}" };
}

public sealed record FormDefinition(
    string Name,
    string? Title,
    string? SuccessMessage,
    IReadOnlyList<FormFieldDefinition> Fields);

public sealed record FormFieldDefinition(
    string Name,
    string? Label,
    string Type,
    bool Required,
    int MaxLength);
