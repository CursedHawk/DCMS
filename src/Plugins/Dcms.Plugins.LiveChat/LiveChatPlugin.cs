using Dcms.PluginSdk.Abstractions;
using Microsoft.Extensions.DependencyInjection;

namespace Dcms.Plugins.LiveChat;

/// <summary>
/// AI Chatbot: an assistant embedded on the published visitor site that answers
/// questions automatically, grounded in the tenant's published content (RAG over
/// the search index). Conversations still surface in the admin chat console, where
/// a human agent can take over — once an agent replies, the bot stays silent for
/// that conversation (see <c>humanHandoff</c>).
///
/// The plugin id stays <c>live-chat</c> for backward compatibility with existing
/// installs; only the display name and behaviour change. The realtime hub, the
/// bot responder and the delivery endpoints all live in content-api — this class
/// contributes the manifest (name + config schema) the admin renders.
/// </summary>
public sealed class LiveChatPlugin : IPlugin
{
    public const string PluginId = "live-chat";

    private const string ConfigSchema = """
        {
          "type": "object",
          "properties": {
            "botName": {
              "type": "string",
              "title": "Assistant name",
              "description": "Shown on the assistant's replies.",
              "default": "Assistant"
            },
            "heading": {
              "type": "string",
              "title": "Widget heading",
              "default": "Chat with us"
            },
            "greeting": {
              "type": "string",
              "title": "Greeting",
              "description": "The first message the assistant shows when the widget opens.",
              "default": "Hi! How can I help you today?"
            },
            "instructions": {
              "type": "string",
              "title": "Instructions & persona",
              "description": "Extra guidance for the assistant: tone of voice, what it should and shouldn't do, and any key facts (opening hours, contact, policies)."
            },
            "grounding": {
              "type": "boolean",
              "title": "Answer from site content",
              "description": "Let the assistant search this site's published content to ground its answers.",
              "default": true
            },
            "humanHandoff": {
              "type": "boolean",
              "title": "Allow human takeover",
              "description": "When an agent replies in the admin console, the assistant stops auto-answering that conversation.",
              "default": true
            }
          },
          "additionalProperties": false
        }
        """;

    public PluginManifest Manifest { get; } = PluginManifest.Create(
        id: PluginId,
        name: "AI Chatbot",
        description: "An AI assistant on your website that answers visitors automatically, grounded in your published content, with human takeover from the admin chat console.",
        allowMultipleInstances: false,
        configJsonSchema: ConfigSchema,
        permissions:
        [
            new PermissionDefinition("read", "View chat conversations"),
            new PermissionDefinition("write", "Reply as an agent and configure the assistant"),
        ],
        category: "Engagement",
        summary: "Live chat between site visitors and your team.",
        iconName: "MessagesSquare");

    public void ConfigureServices(IServiceCollection services)
    {
        // The bot responder and hub are owned by content-api's host.
    }

    public void MapEndpoints(IPluginEndpointBuilder endpoints)
    {
        // Delivery (history replay) + the realtime hub are mapped by content-api.
    }

    public OpenApiFragment BuildOpenApiFragment(PluginInstanceContext instance)
        => OpenApiFragment.Empty;
}
