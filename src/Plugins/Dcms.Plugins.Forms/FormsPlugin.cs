using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using System.Text.Json;
using System.Text.Json.Nodes;
using Dcms.Plugins.VisitorAuth.Api;
using Dcms.Plugins.Forms.Api;
using Dcms.PluginSdk.Abstractions;
using Dcms.PluginSdk.Abstractions.Contracts;
using Dcms.PluginSdk.Abstractions.Data;
using Dcms.PluginSdk.Abstractions.Platform;
using Dcms.Shared.Security;

namespace Dcms.Plugins.Forms;

/// <summary>
/// Visitor-submitted forms (contact, booking, sign-up). Forms are declared in the
/// instance config rather than as content types, because a submission is a write
/// from the public site, not published content.
///
/// The plugin owns its submit route (<see cref="FormSubmissionEndpoints"/>) and reaches
/// email, admin notifications and events through platform contracts. It provides
/// <c>forms.submissions@1</c> and publishes <c>form.submitted</c>, so another plugin (a
/// newsletter, a CRM sync) can act on a submission without knowing how Forms stores one.
/// </summary>
public sealed class FormsPlugin : IPlugin
{
    public const string PluginId = "forms";

    /// <summary>
    /// CORS policy for submissions from an externally hosted tenant site: any origin, no
    /// credentials — a submission carries no cookie or token for a hostile origin to ride on.
    /// Registered by content-api.
    /// </summary>
    public const string SubmitCorsPolicy = "forms-submit";

    /// <summary>Who may read submissions: the platform's content-read, as the admin inbox requires.</summary>
    public const string SubmissionsReadPermission = FormsPermissions.SubmissionsRead;

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
                        "maxLength": { "type": "integer", "minimum": 1, "maximum": 10000, "default": 2000 },
                        "prefill": {
                          "type": "string",
                          "title": "Prefill from visitor",
                          "description": "Fill this field from the signed-in visitor when left empty: visitor.email, visitor.displayName or visitor.<attribute> (attributes VisitorAuth shares with plugins).",
                          "pattern": "^visitor\\.[a-zA-Z][a-zA-Z0-9]*$"
                        }
                      },
                      "required": ["name"],
                      "additionalProperties": false
                    }
                  },
                  "notify": {
                    "type": "object",
                    "title": "Email notification",
                    "description": "Optionally email a copy of every submission. Submissions are stored either way, and preview (sandbox) submissions never send mail.",
                    "properties": {
                      "enabled": {
                        "type": "boolean",
                        "title": "Email me new submissions",
                        "default": false
                      },
                      "recipients": {
                        "type": "array",
                        "title": "Recipients",
                        "description": "Where to send the notification. Each entry is one email address.",
                        "items": { "type": "string", "format": "email" }
                      },
                      "subject": {
                        "type": "string",
                        "title": "Subject",
                        "description": "Defaults to \"New <form> submission\"."
                      },
                      "replyToField": {
                        "type": "string",
                        "title": "Reply-to field",
                        "description": "Name of an email field on this form; its value becomes the notification's Reply-To so you can answer the visitor directly."
                      }
                    },
                    "additionalProperties": false
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
        ],
        category: "Engagement",
        summary: "Contact and signup forms, with submissions in an inbox.",
        iconName: "Inbox",
        provides: [ContractProvision.Of<IFormSubmissions, FormSubmissions>()],
        consumes:
        [
            ContractRequirement.Of<IPluginEmail>(),
            ContractRequirement.Of<IPluginNotifications>(),
            ContractRequirement.Of<IPluginEvents>(),
            // Links a submission to the signed-in visitor and prefills fields when present.
            ContractRequirement.Of<IVisitorIdentity>(optional: true),
        ],
        dataSets:
        [
            // The same gates as the Forms inbox, so roles that review submissions there can here.
            DataSetDeclaration.Of<SubmissionsDataSet>("submissions", "Submissions",
                "What visitors sent through this instance's forms.",
                readPermission: FormsPermissions.SubmissionsRead, writePermission: PlatformPermissions.ContentWrite,
                iconName: "Inbox"),
        ]);


    public void ConfigureServices(IServiceCollection services, PluginHost host)
    {
        if (host.IsSite)
        {
            // Submissions carry no cookie or token, so any origin may post: externally hosted
            // tenant sites can use the forms.
            services.AddCors(o => o.AddPolicy(SubmitCorsPolicy, policy => policy
                .AllowAnyOrigin()
                .AllowAnyHeader()
                .WithMethods("POST")));
        }
    }

    public void MapHostEndpoints(IEndpointRouteBuilder app, PluginHost host)
    {
        if (host.IsAdmin)
        {
            FormReviewEndpoints.Map(app);
        }
    }

    public void MapEndpoints(IPluginEndpointBuilder endpoints) => FormSubmissionEndpoints.Map(endpoints);

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
                SuccessStatus: "202",
                ClientPath: ["forms", form.Name, "submit"],
                // Marks a form for the generated client's `forms` metadata export.
                Extensions: new Dictionary<string, JsonNode> { ["x-dcms-form"] = form.Name }));
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
                        field.TryGetProperty("maxLength", out var max) && max.TryGetInt32(out var maxLen) ? maxLen : 2000,
                        GetString(field, "prefill")));
                }
            }

            result.Add(new FormDefinition(
                name,
                GetString(form, "title"),
                GetString(form, "successMessage"),
                fields,
                ReadNotification(form)));
        }
        return result;
    }

    /// <summary>
    /// The form's notification settings, or null when it has none configured.
    /// A form with notifications enabled but no recipients yields null too —
    /// there is nowhere to send, and a half-filled config is expected while an
    /// operator is still editing it.
    /// </summary>
    private static FormNotificationDefinition? ReadNotification(JsonElement form)
    {
        if (!form.TryGetProperty("notify", out var notify) || notify.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        if (!notify.TryGetProperty("enabled", out var enabled) || enabled.ValueKind != JsonValueKind.True)
        {
            return null;
        }

        var recipients = new List<string>();
        if (notify.TryGetProperty("recipients", out var list) && list.ValueKind == JsonValueKind.Array)
        {
            foreach (var entry in list.EnumerateArray())
            {
                if (entry.ValueKind == JsonValueKind.String && entry.GetString() is { Length: > 0 } address &&
                    !string.IsNullOrWhiteSpace(address))
                {
                    recipients.Add(address.Trim());
                }
            }
        }

        return recipients.Count == 0
            ? null
            : new FormNotificationDefinition(recipients, GetString(notify, "subject"), GetString(notify, "replyToField"));
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
                // The form's own field type. The JSON type above cannot tell a textarea from a
                // one-line input, and a generated form has to render the right one.
                ["x-dcms-field"] = field.Type,
            };
            if (field.Required)
            {
                required.Add(field.Name);
            }
        }

        var schema = new JsonObject
        {
            ["type"] = "object",
            ["title"] = form.Title ?? form.Name,
            ["properties"] = properties,
        };
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
    IReadOnlyList<FormFieldDefinition> Fields,
    FormNotificationDefinition? Notify = null);

/// <summary>
/// Where to email a copy of each submission. Only present when the form has
/// notifications enabled and at least one recipient.
/// </summary>
public sealed record FormNotificationDefinition(
    IReadOnlyList<string> Recipients,
    string? Subject,
    string? ReplyToField);

public sealed record FormFieldDefinition(
    string Name,
    string? Label,
    string Type,
    bool Required,
    int MaxLength,
    string? Prefill = null);
