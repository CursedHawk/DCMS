using Dcms.Plugins.LiveChat.Api;
using Dcms.PluginSdk.Abstractions.Contracts;
using Dcms.Shared.Data.Chat;

namespace Dcms.Plugins.LiveChat;

/// <summary><c>live-chat.moderation@1</c>: the console's close and delete, through the same data set.</summary>
public sealed class LiveChatModeration(ChatDbContext db) : ILiveChatModeration
{
    public async Task CloseAsync(ConversationRef input, CancellationToken ct) =>
        await new ConversationsDataSet(db).RunActionAsync("close", [input.ConversationId.ToString()], null, ct);

    public async Task DeleteAsync(ConversationRef input, CancellationToken ct)
    {
        if (!await new ConversationsDataSet(db).DeleteAsync(input.ConversationId.ToString(), ct))
        {
            throw new ContractValidationException($"No conversation {input.ConversationId}.");
        }
    }
}
