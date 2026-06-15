namespace Dcms.Shared.Contracts.Events;

public sealed record ChatMessagePosted(
    Guid EventId,
    DateTimeOffset OccurredAt,
    Guid TenantId,
    Guid ConversationId,
    Guid MessageId,
    string Sender) : IDcmsEvent
{
    public int Version => 1;
}
