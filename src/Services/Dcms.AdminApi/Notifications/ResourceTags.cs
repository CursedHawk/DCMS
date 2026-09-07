namespace Dcms.AdminApi.Notifications;

/// <summary>
/// The vocabulary of "something in this tenant changed" signals pushed to open consoles.
///
/// <para>A tag names a <b>class of data</b>, not an event and not a row: the SPA maps each one
/// onto the react-query keys that would now be stale and refetches them. That is the whole
/// contract, and it is why the list is short. A tag per event would put the decision of what to
/// refetch on the producer, which does not know what any console is currently showing.</para>
///
/// <para>These are <b>not</b> notifications. A notification is addressed to people, is stored,
/// is permission-scoped at raise time and may interrupt with a toast. A resource change is
/// addressed to screens, is not stored, and never interrupts — it makes a table correct itself.
/// The two travel over the same hub because the same connection is already open.</para>
///
/// <para><b>Broadcast to the whole tenant, not per user.</b> A tag carries no data — only the
/// name of a class of thing — so it discloses nothing that the recipient's next request would
/// not already be refused. The refetch it triggers is an ordinary authorised call, and a member
/// who may not read media simply has no media query to invalidate.</para>
///
/// <para>The strings are duplicated in the SPA's `live` module. A tag either side does not know
/// degrades to "nothing refetches", which is the correct failure for a console that may be
/// older or newer than the server it is talking to.</para>
/// </summary>
public static class ResourceTags
{
    public const string Content = "content";
    public const string Media = "media";
    public const string Sites = "sites";
    public const string Builds = "builds";
    public const string Plugins = "plugins";
    public const string Domains = "domains";
    public const string Members = "members";
    public const string Invitations = "invitations";
    public const string Forms = "forms";
    public const string Chat = "chat";
    public const string Analytics = "analytics";

    /// <summary>
    /// The class of data a notification kind implies has changed.
    ///
    /// <para>Deriving this from the kind rather than asking every producer for it is what makes
    /// the whole feature cost one file: every asynchronous outcome that was already worth
    /// telling somebody about is already routed through <see cref="NotificationPublisher"/>.</para>
    ///
    /// <para>A kind with no entry pushes no resource change, which is right for the ones that
    /// change nothing a screen is showing.</para>
    /// </summary>
    public static string? ForKind(string kind) => kind switch
    {
        NotificationKinds.SitePublished => Builds,
        NotificationKinds.SiteBuildFailed => Builds,
        NotificationKinds.MediaProcessed => Media,
        NotificationKinds.MediaFailed => Media,
        NotificationKinds.ContentPublished => Content,
        NotificationKinds.ContentUnpublished => Content,
        NotificationKinds.InvitationAccepted => Members,
        NotificationKinds.InvitationExpired => Invitations,
        NotificationKinds.MemberRoleChanged => Members,
        NotificationKinds.DomainVerified => Domains,
        NotificationKinds.PluginInstanceChanged => Plugins,
        NotificationKinds.FormSubmitted => Forms,
        NotificationKinds.ChatConversationStarted => Chat,
        // A Meta token nearing expiry changes no list anyone is looking at; the notification
        // itself is the entire point.
        NotificationKinds.SocialTokenExpiring => null,
        _ => null,
    };
}
