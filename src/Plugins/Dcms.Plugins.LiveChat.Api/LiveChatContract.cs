using Dcms.PluginSdk.Abstractions;
using Dcms.PluginSdk.Abstractions.Contracts;

namespace Dcms.Plugins.LiveChat.Api;

public static class ChatPermissions
{
    /// <summary>The platform permission the chat console uses.</summary>
    public const string Read = "chat:read";
}

/// <param name="Status">"Open" or "Closed"; all when null.</param>
public sealed record ConversationQuery(string? Status = null, int Limit = 50);

public sealed record ConversationSummary(
    Guid Id, string VisitorName, string Status, DateTimeOffset CreatedAt, DateTimeOffset LastMessageAt);

public sealed record ConversationList(IReadOnlyList<ConversationSummary> Items);

public sealed record ConversationRef(Guid ConversationId);

/// <param name="Sender">"Visitor", "Agent" or "Bot".</param>
public sealed record ChatLine(Guid Id, string Sender, string Body, DateTimeOffset SentAt);

public sealed record ConversationMessages(Guid ConversationId, IReadOnlyList<ChatLine> Messages);

/// <summary>A visitor wrote the first message of a conversation: someone is waiting.</summary>
[ContractEvent("live-chat.conversation.started")]
public sealed record ChatConversationStarted(Guid ConversationId, string VisitorName) : IPluginEvent;

/// <summary>Any message in a conversation — from the visitor, an agent or the assistant.</summary>
[ContractEvent("live-chat.message.received")]
public sealed record ChatMessageReceived(Guid ConversationId, Guid MessageId, string Sender) : IPluginEvent;

/// <summary>The tenant's site chat conversations (previews excluded).</summary>
[DcmsContract("live-chat.conversations", 1,
    Description = "Site chat conversations between visitors, agents and the AI assistant.",
    Events = [typeof(ChatConversationStarted), typeof(ChatMessageReceived)])]
public interface ILiveChat
{
    [Operation(OpRisk.Read, Permission = ChatPermissions.Read, Expose = OpExposure.Admin | OpExposure.Ai,
        ReturnsExternalText = true, Description = "Conversations, most recently active first.")]
    Task<ConversationList> ListAsync(ConversationQuery input, CancellationToken ct);

    [Operation(OpRisk.Read, Permission = ChatPermissions.Read, Expose = OpExposure.Admin | OpExposure.Ai,
        ReturnsExternalText = true, Description = "One conversation's messages, oldest first.")]
    Task<ConversationMessages> GetMessagesAsync(ConversationRef input, CancellationToken ct);
}
