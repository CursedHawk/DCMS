using Dcms.PluginSdk.Abstractions;
using Dcms.PluginSdk.Abstractions.Contracts;
using Dcms.PluginSdk.Abstractions.Platform;
using Dcms.Plugins.LiveChat.Api;
using Dcms.Shared.Audit.Http;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using StackExchange.Redis;

namespace Dcms.Plugins.LiveChat;

/// <summary>
/// AI Chatbot: an assistant embedded on the published visitor site that answers
/// questions automatically, grounded in the tenant's published content (RAG over
/// the search index). Conversations still surface in the admin chat console, where
/// a human agent can take over — once an agent replies, the bot stays silent for
/// that conversation (see <c>humanHandoff</c>).
///
/// The plugin id stays <c>live-chat</c> for backward compatibility with existing
/// installs. The plugin owns the whole of it: the realtime hub, the bot responder and the
/// history replay on the site plane; the agent console and the new-conversation notice on
/// the admin plane; and <see cref="ILiveChat"/> for everyone else.
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
        clientBindings:
        [
            new ClientBinding(["chat", "history"],
                "(conversationId: string): Promise<ChatMessage[]> => http.chatHistory({slugLiteral}, conversationId)",
                "api.{member}.chat.history(conversationId)", "ChatMessage[]",
                "GET /api/{slug}/chat/conversations/{id}/messages", "Messages of one chat conversation.", ["ChatMessage"]),
        ],
        category: "Engagement",
        summary: "Live chat between site visitors and your team.",
        iconName: "MessagesSquare",
        provides: [ContractProvision.Of<ILiveChat, LiveChatConversations>()],
        consumes:
        [
            // Grounds the bot's answers in the tenant's published content.
            ContractRequirement.Of<IPluginSearch>(),
            ContractRequirement.Of<IPluginEvents>(),
            ContractRequirement.Of<IPluginNotifications>(),
        ],
        subscribes: [EventSubscription.Of<ChatConversationStarted, NotifyAgentsOfNewConversation>()]);

    public void ConfigureServices(IServiceCollection services, PluginHost host)
    {
        if (!host.IsSite)
        {
            return;
        }
        // SignalR with a Redis backplane so message fan-out crosses replicas.
        var signalR = services.AddSignalR();
        var redis = host.Configuration.GetConnectionString("Redis");
        if (!string.IsNullOrWhiteSpace(redis))
        {
            signalR.AddStackExchangeRedis(redis + ",abortConnect=false",
                options => options.Configuration.ChannelPrefix = RedisChannel.Literal("dcms-chat"));
        }
        services.AddSingleton<ChatBotResponder>();
    }

    /// <summary>History replay when the widget (re)opens: <c>/api/{slug}/chat/conversations/{id}/messages</c>.</summary>
    public void MapEndpoints(IPluginEndpointBuilder endpoints) => ChatHistoryEndpoints.Map(endpoints);

    public void MapHostEndpoints(IEndpointRouteBuilder app, PluginHost host)
    {
        if (host.IsSite)
        {
            // Tenant-wide: the connection names its tenant in the query string.
            app.MapHub<ChatHub>("/hub/chat")
                .AuditExempt("SignalR transport endpoint, not an action. Chat messages are recorded by the "
                           + "hub methods that write them, where the conversation and author are known.");
        }
        else
        {
            ChatConsoleEndpoints.Map(app);
        }
    }
}
