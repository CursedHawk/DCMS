namespace Dcms.AdminApi.Notifications;

/// <summary>
/// The catalogue of notification kinds. A kind is the join between three things that must
/// agree: the permission that decides the audience, the i18n keys the SPA renders, and the
/// icon it picks. Keeping them in one file means adding a notification is one edit here plus
/// two locale entries, rather than a permission chosen ad hoc at each call site.
///
/// <para>Kind strings are stable and stored — renaming one orphans the notifications already
/// in the table, whose <c>TitleKey</c> would no longer resolve. Add a new kind instead.</para>
///
/// <para>The i18n keys follow the kind: <c>notifications.kinds.{kind}.title</c> and
/// <c>.body</c>, so a missing translation is obvious in the UI rather than silent.</para>
/// </summary>
public static class NotificationKinds
{
    // Sites / deployment
    public const string SitePublished = "site.published";
    public const string SiteBuildFailed = "site.build.failed";

    // Media
    public const string MediaProcessed = "media.processed";
    public const string MediaFailed = "media.failed";

    // Content
    public const string ContentPublished = "content.published";
    public const string ContentUnpublished = "content.unpublished";

    // Tenancy / access
    public const string InvitationAccepted = "invitation.accepted";
    public const string InvitationExpired = "invitation.expired";
    public const string MemberRoleChanged = "member.role.changed";
    public const string DomainVerified = "domain.verified";
    public const string PluginInstanceChanged = "plugin.instance.changed";

    // Engagement / integrations
    public const string FormSubmitted = "form.submitted";
    public const string ChatConversationStarted = "chat.conversation.started";
    public const string SocialTokenExpiring = "social.token.expiring";

    /// <summary>
    /// Raised by a plugin through <c>dcms.notifications@1</c>. Plugins have no entries in the
    /// SPA's locale bundles, so they supply the prose and this kind's keys just render it
    /// (<c>{{title}}</c> / <c>{{body}}</c>); the plugin id rides in the params.
    /// </summary>
    public const string PluginMessage = "plugin.message";

    /// <summary>
    /// A form submission, raised by the Forms plugin since it owns its code (docs/adr/0016).
    /// Supersedes <see cref="FormSubmitted"/>, which stays catalogued for the rows already stored.
    /// Plugin kinds are always <c>plugin.{pluginId}.{name}</c>.
    /// </summary>
    public const string FormsSubmitted = "plugin.forms.submitted";

    /// <summary>
    /// A visitor opened a chat conversation, raised by the LiveChat plugin's own subscription to
    /// <c>live-chat.conversation.started</c>. Supersedes <see cref="ChatConversationStarted"/>,
    /// which stays catalogued for the rows already stored.
    /// </summary>
    public const string LiveChatConversationStarted = "plugin.live-chat.conversation-started";

    /// <summary>
    /// A Meta connection can no longer be renewed, raised by whichever feed plugin the tenant
    /// uses. Supersedes <see cref="SocialTokenExpiring"/>, kept for the rows already stored.
    /// </summary>
    public const string InstagramTokenExpiring = "plugin.instagram.token-expiring";

    /// <inheritdoc cref="InstagramTokenExpiring"/>
    public const string FacebookTokenExpiring = "plugin.facebook.token-expiring";

    /// <summary>
    /// The i18n key for a kind's title.
    ///
    /// <para><b>The dots in a kind are replaced with underscores, and that is load-bearing.</b>
    /// i18next treats "." as its key separator, so <c>notifications.kinds.site.published.title</c>
    /// would be resolved as a five-level nested lookup — kinds → site → published → title —
    /// and never find a flat <c>"site.published"</c> entry. Every notification would then
    /// render its own key instead of its text. Underscoring keeps the stored kind string
    /// stable while giving i18next a single flat segment to match.</para>
    /// </summary>
    public static string TitleKey(string kind) => $"notifications.kinds.{Slug(kind)}.title";

    /// <inheritdoc cref="TitleKey"/>
    public static string BodyKey(string kind) => $"notifications.kinds.{Slug(kind)}.body";

    /// <summary>The i18n-safe form of a kind: dots are the key separator, so they cannot survive.</summary>
    public static string Slug(string kind) => kind.Replace('.', '_');
}
