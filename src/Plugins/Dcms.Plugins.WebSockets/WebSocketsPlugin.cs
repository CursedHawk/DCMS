using Dcms.Plugins.UserAuth.Api;
using Dcms.Plugins.VisitorAuth.Api;
using Dcms.Plugins.WebSockets.Api;
using Dcms.PluginSdk.Abstractions;
using Dcms.PluginSdk.Abstractions.Contracts;
using Dcms.PluginSdk.Abstractions.Platform;
using Dcms.Shared.Audit.Http;
using Dcms.Shared.Security;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using System.Text.Json.Nodes;

namespace Dcms.Plugins.WebSockets;

/// <summary>
/// Realtime channels for a tenant's sites (ADR 0037). Browsers connect to <c>/hub/ws</c> on the
/// site's own domain and subscribe to the instance's channels; functions and plugins send through
/// <c>websockets.channels@1</c>; a browser publish can be raised as
/// <c>websockets.message.received</c>.
///
/// <para>Each channel's payload has an optional JSON Schema, in a subset every consumer can read:
/// the visual builder, the TypeScript and C# generators, and the validation of every message.
/// The subset is enforced by this config schema (<c>$defs/payload</c>), so a config the console,
/// the assistant or the CLI saves is always one the generators can type.</para>
/// </summary>
public sealed class WebSocketsPlugin : IPlugin
{
    public const string PluginId = "websockets";

    /// <summary>The hub's path on a site's domain. Not under <c>/api/hub</c>: that is admin-api's.</summary>
    public const string HubPath = "/hub/ws";

    internal const string ConfigSchema = """
        {
          "type": "object",
          "properties": {
            "channels": {
              "type": "array",
              "title": "Channels",
              "description": "What browsers can subscribe to. Each channel's payload type flows into the site's generated client and the Functions SDK.",
              "maxItems": 100,
              "items": {
                "type": "object",
                "properties": {
                  "name": {
                    "type": "string", "title": "Name",
                    "description": "Lowercase letters, digits and hyphens. A keyed channel is subscribed to as name/key.",
                    "pattern": "^[a-z0-9][a-z0-9-]{0,62}$"
                  },
                  "description": { "type": "string", "title": "Description", "maxLength": 500 },
                  "keyed": {
                    "type": "boolean", "title": "Keyed", "default": false,
                    "description": "Split the channel by a key: a chat room, an order, a user."
                  },
                  "subscribe": {
                    "type": "string", "title": "Who may subscribe", "default": "public",
                    "enum": ["public", "signedIn", "permission", "self"],
                    "description": "public: anyone. signedIn: a signed-in site user or visitor. permission: a site user whose role grants this channel's subscribe permission. self: a keyed channel whose key is the caller's own id."
                  },
                  "publish": {
                    "type": "string", "title": "Who may publish from a browser", "default": "none",
                    "enum": ["none", "subscribers", "permission"],
                    "description": "Functions and plugins can always send. none: only they can. subscribers: whoever may subscribe. permission: a site user whose role grants this channel's publish permission."
                  },
                  "raiseEvent": {
                    "type": "boolean", "title": "Raise an event", "default": false,
                    "description": "Also raise websockets.message.received for what browsers publish, so functions (on.websocket) and plugins can act on it."
                  },
                  "schema": { "$ref": "#/$defs/payload", "title": "Payload schema" }
                },
                "required": ["name"],
                "additionalProperties": false
              }
            }
          },
          "additionalProperties": false,
          "$defs": {
            "payload": {
              "type": "object",
              "description": "A JSON Schema in the subset every generator can type.",
              "properties": {
                "type": { "enum": ["object", "array", "string", "number", "integer", "boolean"] },
                "description": { "type": "string", "maxLength": 500 },
                "properties": { "type": "object", "additionalProperties": { "$ref": "#/$defs/payload" } },
                "required": { "type": "array", "items": { "type": "string" } },
                "additionalProperties": { "type": "boolean" },
                "items": { "$ref": "#/$defs/payload" },
                "enum": { "type": "array", "minItems": 1, "items": { "type": ["string", "number", "boolean"] } },
                "const": { "type": ["string", "number", "boolean"] },
                "format": { "enum": ["date-time", "date", "email", "uri", "uuid"] },
                "minLength": { "type": "integer", "minimum": 0 },
                "maxLength": { "type": "integer", "minimum": 0 },
                "minimum": { "type": "number" },
                "maximum": { "type": "number" },
                "minItems": { "type": "integer", "minimum": 0 },
                "maxItems": { "type": "integer", "minimum": 0 }
              },
              "additionalProperties": false
            }
          }
        }
        """;

    public PluginManifest Manifest { get; } = PluginManifest.Create(
        id: PluginId,
        name: "WebSockets",
        description: "Realtime channels for your sites: notifications, chat and live updates. Each channel's payload type is part of "
                     + "the site's generated client and the Functions SDK; functions and plugins send, and can act on what browsers send.",
        allowMultipleInstances: true,
        configJsonSchema: ConfigSchema,
        permissions:
        [
            new PermissionDefinition("send", "Send realtime messages",
                "Send to a channel's subscribers or to one site user's connections, from functions and plugins."),
            new PermissionDefinition("read", "Act on realtime messages",
                "Run functions on what browsers publish to channels that raise events."),
            new PermissionDefinition("manage", "Manage channels", "Add, change and remove channels and their payload types."),
        ],
        category: "Engagement",
        summary: "Typed realtime channels: notifications, chat and live updates.",
        iconName: "RadioTower",
        // Channels whose access is "permission" are granted to site users with the site roles.
        provides:
        [
            ContractProvision.Of<IWebSocketChannels, ChannelSender>(),
            ContractProvision.Of<IUserResources, ChannelUserResources>(),
        ],
        consumes:
        [
            ContractRequirement.Of<IPluginEvents>(),
            // Who the caller is (VisitorAuth visitors and User Authentication users alike) and
            // which channel permissions their roles grant; anonymous without them.
            ContractRequirement.Of<IVisitorIdentity>(optional: true),
            ContractRequirement.Of<IUserAccess>(optional: true),
        ],
        adminScreens: [AdminScreen.Settings]);

    /// <summary>
    /// <c>send</c> and <c>sendToUser</c> as unions over this instance's channels, each with its own
    /// payload type, so the Functions SDK types <c>send({ channel: "chat", key, payload })</c> per
    /// channel (TypeScript) and gives one overload per channel (C#). The implementation still
    /// validates every payload against the channel's schema.
    /// </summary>
    public IReadOnlyDictionary<string, JsonNode> OperationInputSchemas(PluginInstanceContext instance)
    {
        var channels = Channels.Read(instance.Config.RootElement);
        if (channels.Count == 0)
        {
            return new Dictionary<string, JsonNode>();
        }
        return new Dictionary<string, JsonNode>
        {
            ["websockets.channels.Send"] = Union(channels, c =>
            {
                var (props, required) = Common(c);
                if (c.Keyed)
                {
                    props["key"] = new JsonObject { ["type"] = "string", ["description"] = "Which " + c.Name + " to send to." };
                    required.Add("key");
                }
                return (props, required);
            }),
            ["websockets.channels.SendToUser"] = Union(channels, c =>
            {
                var (props, required) = Common(c);
                props["to"] = new JsonObject { ["type"] = "string", ["format"] = "uuid", ["description"] = "The site user's or visitor's id." };
                required.Add("to");
                return (props, required);
            }),
        };

        static (JsonObject Props, JsonArray Required) Common(ChannelDefinition c) => (
            new JsonObject
            {
                ["channel"] = new JsonObject { ["const"] = c.Name },
                ["payload"] = c.Schema is { } schema ? JsonNode.Parse(schema.GetRawText())! : new JsonObject(),
            },
            new JsonArray("channel", "payload"));

        static JsonNode Union(IEnumerable<ChannelDefinition> channels, Func<ChannelDefinition, (JsonObject Props, JsonArray Required)> branch) =>
            new JsonObject
            {
                ["oneOf"] = new JsonArray(channels.Select(c =>
                {
                    var (props, required) = branch(c);
                    return (JsonNode)new JsonObject
                    {
                        ["type"] = "object",
                        ["description"] = c.Description ?? $"The {c.Name} channel.",
                        ["properties"] = props,
                        ["required"] = required,
                        ["additionalProperties"] = false,
                    };
                }).ToArray()),
            };
    }

    /// <summary>
    /// The channels for the tenant's OpenAPI document and generated site client (ADR 0037): no HTTP
    /// operations, a <c>Realtime</c> surface naming the hub and each channel's access, and each
    /// channel's payload schema as <c>{slug}_{channel}_payload</c>.
    /// </summary>
    public OpenApiFragment BuildOpenApiFragment(PluginInstanceContext instance)
    {
        var schemas = new Dictionary<string, JsonNode>();
        var channels = new JsonObject();
        foreach (var channel in Channels.Read(instance.Config.RootElement))
        {
            JsonNode payload = new JsonObject();
            if (channel.Schema is { } schema)
            {
                var name = $"{instance.Slug}_{channel.Name}_payload";
                schemas[name] = JsonNode.Parse(schema.GetRawText())!;
                payload = new JsonObject { ["$ref"] = $"#/components/schemas/{name}" };
            }
            channels[channel.Name] = new JsonObject
            {
                ["description"] = channel.Description,
                ["keyed"] = channel.Keyed,
                ["subscribe"] = channel.Subscribe,
                ["publish"] = channel.Publish,
                ["payload"] = payload,
            };
        }
        return OpenApiFragment.Empty with
        {
            Schemas = schemas,
            Realtime = new JsonObject { ["hub"] = $"{HubPath}?instance={instance.Slug}", ["channels"] = channels },
        };
    }

    public void ConfigureServices(IServiceCollection services, PluginHost host)
    {
        services.Configure<WebSocketsOptions>(host.SettingsFor(PluginId));
        // Every plane: the site plane serves the hub, the admin plane sends through IHubContext.
        WebSocketsBackplane.Register(services, host.Configuration);
        if (host.IsSite)
        {
            services.AddSignalR().AddHubOptions<WebSocketsHub>(o =>
            {
                // A browser frame; a server send is capped at Channels.MaxPayloadBytes.
                o.MaximumReceiveMessageSize = 16 * 1024;
                // Below YARP's 100 s activity timeout on the edge and site-host hops.
                o.KeepAliveInterval = TimeSpan.FromSeconds(15);
                o.EnableDetailedErrors = false;
            });
        }
    }

    public void MapHostEndpoints(IEndpointRouteBuilder app, PluginHost host)
    {
        if (!host.IsSite)
        {
            return;
        }
        // WebSockets only: the identity and permission contracts read the upgrade request, which
        // stays open for the connection's life only on this transport.
        app.MapHub<WebSocketsHub>(HubPath, o => o.Transports = HttpTransportType.WebSockets)
            .PermissionExempt("Each hub method checks the channel's own access rule (public, signed in, a site permission, self).")
            .AuditExempt("Realtime transport, not an action. Channel changes are audited where they are made; messages are not recorded.");
    }
}
