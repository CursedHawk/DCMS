namespace Dcms.Shared.Contracts.Events;

/// <summary>
/// A notification was recorded for the platform's operators.
///
/// <para>Carries the <b>kind</b> and not the content, and that is the whole design. The row is
/// already written and the console reads it over an authorised REST call; what this event has
/// to say is only "there is news, and it is news of this sort" — enough for the console's API
/// to work out which of its pages just went stale. A payload would be a second copy of the
/// notification that can disagree with the row, travelling to a service that holds no grant on
/// the table it came from.</para>
///
/// <para>Published on the TENANCY stream, which already carries the platform's control plane
/// (<c>plugin.instance.&gt;</c>, <c>membership.&gt;</c>, <c>edge.&gt;</c>) rather than strictly
/// tenancy. A stream of its own would be more infrastructure than single figures of messages a
/// week deserve.</para>
/// </summary>
public sealed record PlatformNotificationRaised(
    Guid EventId,
    DateTimeOffset OccurredAt,
    Guid NotificationId,
    string Kind,
    string Severity) : IDcmsEvent
{
    public int Version => 1;
}
