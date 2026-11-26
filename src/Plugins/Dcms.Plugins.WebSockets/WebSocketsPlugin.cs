using Dcms.PluginSdk.Abstractions;

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
                    "enum": ["public", "signedIn", "roles", "self"],
                    "description": "public: anyone. signedIn: a signed-in site user or visitor. roles: a user holding one of the roles below. self: a keyed channel whose key is the caller's own id."
                  },
                  "subscribeRoles": { "type": "array", "title": "Subscriber roles", "items": { "type": "string", "minLength": 1 } },
                  "publish": {
                    "type": "string", "title": "Who may publish from a browser", "default": "none",
                    "enum": ["none", "subscribers", "roles"],
                    "description": "Functions and plugins can always send. none: only they can. subscribers: whoever may subscribe. roles: a user holding one of the roles below."
                  },
                  "publishRoles": { "type": "array", "title": "Publisher roles", "items": { "type": "string", "minLength": 1 } },
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
        adminScreens: [AdminScreen.Settings]);
}
