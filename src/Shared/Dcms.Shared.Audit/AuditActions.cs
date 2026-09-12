using System.Collections.Concurrent;

namespace Dcms.Shared.Audit;

/// <summary>
/// The action taxonomy. Keys are dotted <c>{resource}.{verb}</c> strings rather than an
/// enum so that plugins can contribute their own — the same reason
/// <c>PlatformPermissions</c> keeps <c>plugin:{pluginId}:{action}</c> as a string.
///
/// The platform keys below are the *known* vocabulary, not a closed set: an unregistered
/// key is recorded as-is. Registration exists so the audit UI can label and group actions,
/// not to gate what may be written — dropping a record because its key was unfamiliar
/// would be the one failure mode an audit log must never have.
/// </summary>
public static class AuditActions
{
    // ---- tenancy ----
    public const string TenantCreated = "tenant.created";
    public const string TenantUpdated = "tenant.updated";
    public const string TenantTransferred = "tenant.transferred";

    /// <summary>
    /// Written and confirmed before the purge touches anything. Paired with
    /// <see cref="TenantPurged"/>, which is written after: a start with no matching finish is
    /// how a purge that died partway through announces itself, and there is no other way to
    /// find out, since the rows that would have shown it are the ones that were deleted.
    /// </summary>
    public const string TenantPurgeStarted = "tenant.purge.started";
    public const string TenantPurged = "tenant.purged";

    public const string MemberInvited = "member.invited";
    public const string MemberInviteResent = "member.invite.resent";
    public const string MemberInviteRevoked = "member.invite.revoked";
    public const string MemberInviteAccepted = "member.invite.accepted";
    public const string MemberRoleGranted = "member.role.granted";
    public const string MemberRoleRevoked = "member.role.revoked";
    public const string MemberLeft = "member.left";

    public const string RoleCreated = "role.created";
    public const string RoleUpdated = "role.updated";
    public const string RoleDeleted = "role.deleted";

    public const string DomainAdded = "domain.added";

    /// <summary>Added through the managed path, which skips the TXT challenge — worth telling apart.</summary>
    public const string DomainProvisioned = "domain.provisioned";

    public const string DomainVerified = "domain.verified";
    public const string DomainLinked = "domain.linked";
    public const string DomainPrimarySet = "domain.primary.set";
    public const string DomainRemoved = "domain.removed";

    /// <summary>An operator asked for a certificate to be reissued before it was due.</summary>
    public const string DomainCertificateReissued = "domain.certificate.reissued";
    /// <summary>A tenant installed their own certificate for a domain they had verified.</summary>
    public const string DomainCertificateUploaded = "domain.certificate.uploaded";
    /// <summary>An uploaded certificate was dropped, returning the domain to DCMS-managed issuance.</summary>
    public const string DomainCertificateRemoved = "domain.certificate.removed";

    // ---- managed certificates (the platform's OWN domains, not a tenant's) ----
    //
    // Audited even though the edge's own certificate tables deliberately are not: these are
    // operator decisions about which names this platform will hold TLS for, which is exactly the
    // class of change an audit log exists to answer questions about. The renewals they cause are
    // not audited, for the reason recorded on EdgeDbContext.

    /// <summary>A superadmin added a certificate for the platform to keep renewed.</summary>
    public const string ManagedCertificateCreated = "managed.certificate.created";
    /// <summary>Its identifiers, name or enabled state changed.</summary>
    public const string ManagedCertificateUpdated = "managed.certificate.updated";
    /// <summary>It was removed; whatever certificate it produced stops being renewed.</summary>
    public const string ManagedCertificateRemoved = "managed.certificate.removed";
    /// <summary>A superadmin asked for one to be reissued before it was due.</summary>
    public const string ManagedCertificateReissued = "managed.certificate.reissued";

    // ---- content ----
    public const string ContentCreated = "content.created";
    public const string ContentUpdated = "content.updated";
    public const string ContentDeleted = "content.deleted";
    public const string ContentPublished = "content.published";
    public const string ContentUnpublished = "content.unpublished";
    public const string ContentScheduled = "content.scheduled";
    public const string ContentScheduleCancelled = "content.schedule.cancelled";

    // ---- media ----
    public const string MediaUploaded = "media.uploaded";
    public const string MediaUpdated = "media.updated";
    public const string MediaMoved = "media.moved";
    public const string MediaDeleted = "media.deleted";
    public const string MediaDownloaded = "media.downloaded";

    /// <summary>The upload landed and the derivative work did not; the asset is unusable.</summary>
    public const string MediaProcessingFailed = "media.processing.failed";
    public const string MediaFolderCreated = "media.folder.created";
    public const string MediaFolderUpdated = "media.folder.updated";
    public const string MediaFolderDeleted = "media.folder.deleted";

    // ---- sites ----
    public const string SiteCreated = "site.created";
    public const string SiteUpdated = "site.updated";
    public const string SiteDeleted = "site.deleted";

    /// <summary>The rows went; the bytes did not. Someone has to be able to find that later.</summary>
    public const string SiteDeleteCleanupFailed = "site.delete.cleanup.failed";
    public const string SitePublishRequested = "site.publish.requested";
    public const string SitePublished = "site.published";
    public const string SiteBuildFailed = "site.build.failed";
    public const string SiteBuildActivated = "site.build.activated";
    public const string SiteBuildCreated = "site.build.created";
    public const string SiteFilesChanged = "site.files.changed";
    public const string SiteUploaded = "site.uploaded";
    public const string SitePreviewReset = "site.preview.reset";

    // ---- git ----
    public const string GitRepoProvisioned = "git.repo.provisioned";
    public const string GitCommitted = "git.committed";
    public const string GitBranchCreated = "git.branch.created";
    public const string GitMerged = "git.merged";
    public const string GitMergeResolved = "git.merge.resolved";
    public const string GitRestored = "git.restored";
    public const string GitPushReceived = "git.push.received";
    public const string GitRepoDeleted = "git.repo.deleted";
    public const string GitCollaboratorChanged = "git.collaborator.changed";

    /// <summary>
    /// A permission change committed but did not reach Forgejo. Best-effort is right — an
    /// outage must not fail the request — but somebody keeping push rights they were just
    /// denied is exactly the thing an audit log is for.
    /// </summary>
    public const string GitAccessReconcileFailed = "git.access.reconcile.failed";

    // ---- plugins ----
    public const string PluginInstanceCreated = "plugin.instance.created";
    public const string PluginInstanceUpdated = "plugin.instance.updated";
    public const string PluginInstanceActioned = "plugin.instance.actioned";

    // ---- forms ----
    public const string FormSubmitted = "form.submitted";
    public const string FormSubmissionRead = "form.submission.read";
    public const string FormSubmissionHandled = "form.submission.handled";
    public const string FormSubmissionDeleted = "form.submission.deleted";
    public const string FormSubmissionsExported = "form.submissions.exported";

    // ---- ai ----
    public const string AiSettingsUpdated = "ai.settings.updated";
    public const string AiCredentialsUpdated = "ai.credentials.updated";
    public const string AiCredentialsDeleted = "ai.credentials.deleted";
    public const string AiCredentialsRead = "ai.credentials.read";
    public const string AiGenerateSite = "ai.generate.site";
    public const string AiGeneratePage = "ai.generate.page";
    public const string AiGenerateBlock = "ai.generate.block";

    /// <summary>A call proxied to the model on the tenant's credentials — who spent the budget.</summary>
    public const string AiRequestProxied = "ai.request";

    /// <summary>
    /// A conversation renamed, shared with the workspace, or archived. Sharing is the part worth
    /// the record: it makes one member's transcript — and every tool result in it — readable by
    /// everyone else.
    /// </summary>
    public const string AiConversationUpdated = "ai.conversation.updated";

    public const string AiConversationDeleted = "ai.conversation.deleted";

    /// <summary>Stored conversations aged out by the retention sweep.</summary>
    public const string AiConversationsPruned = "ai.conversations.pruned";

    // ---- visitors (site end-users, a separate identity plane from platform users) ----
    public const string VisitorRegistered = "visitor.registered";
    public const string VisitorLoggedIn = "visitor.login.succeeded";

    // ---- analytics / chat ----
    public const string AnalyticsPurged = "analytics.purged";
    public const string ChatTranscriptRead = "chat.transcript.read";

    // ---- auth (platform scope: TenantId is null) ----
    public const string LoginSucceeded = "auth.login.succeeded";
    public const string LoginFailed = "auth.login.failed";
    public const string LoginLockedOut = "auth.login.lockedout";
    public const string Logout = "auth.logout";
    public const string PasswordChanged = "auth.password.changed";
    public const string PasswordResetRequested = "auth.password.reset.requested";
    public const string PasswordResetCompleted = "auth.password.reset.completed";
    public const string AccountRegistered = "auth.account.registered";
    public const string AccountDeleted = "auth.account.deleted";

    /// <summary>
    /// The tenant-side half of deleting an account: memberships dropped, per-user rows swept.
    /// Separate from <see cref="AccountDeleted"/>, which identity writes when the login itself
    /// goes — they are two services and either can succeed while the other has not yet run.
    /// </summary>
    public const string AccountDetached = "auth.account.detached";
    public const string SsoLinked = "auth.sso.linked";
    public const string SshKeyAdded = "auth.sshkey.added";
    public const string SshKeyRemoved = "auth.sshkey.removed";
    public const string TokenIssued = "auth.token.issued";

    // ---- social (Meta connections) ----

    /// <summary>
    /// An admin started a Meta consent flow. Recorded at the start, not only on success,
    /// because the callback that completes it is anonymous by necessity — this is the last
    /// point at which we can attribute the connection to a named person.
    /// </summary>
    public const string ConnectionStarted = "social.connection.started";

    public const string ConnectionRevoked = "social.connection.revoked";

    /// <summary>
    /// An admin pressed "sync now". Its own action rather than the generic
    /// <see cref="PluginInstanceActioned"/>: this one spends a tenant's Meta rate budget and
    /// decrypts their credential, and sharing a key with enable/disable would make "who
    /// triggered the sync that hit the rate limit?" unanswerable from the log.
    /// </summary>
    public const string SocialSyncRequested = "social.sync.requested";

    // ---- security ----
    public const string PermissionDenied = "security.permission.denied";
    public const string Unauthenticated = "security.unauthenticated";
    public const string WebhookRejected = "security.webhook.rejected";
    public const string SecretAccessed = "security.secret.accessed";

    /// <summary>
    /// A stored provider credential was <i>not</i> handed to an outbound call, because the
    /// destination had been chosen at a narrower scope than the credential's owner. The
    /// refusal is worth a record for the same reason the successful access is: it is somebody
    /// attempting to spend a key that is not theirs, and it is invisible everywhere else.
    /// </summary>
    public const string SecretAccessDenied = "security.secret.denied";

    // ---- observability ----

    /// <summary>
    /// A monitoring alert fired and was routed to a human. Recorded because the alert itself
    /// lives in Grafana's own state, which is the one piece of observability data on this
    /// platform that is not backed up — and "when did we first know" is a question that gets
    /// asked after every incident.
    /// </summary>
    public const string AlertNotified = "observability.alert.notified";

    /// <summary>
    /// Raw analytics events removed by retention. One record per pass carrying the cutoff and
    /// the count, rather than a bulk-statement record per batch — the batching is an
    /// implementation detail of not holding a long transaction, and twenty records saying "a
    /// table got shorter" is worse than one saying what policy removed how much.
    /// </summary>
    public const string AnalyticsPruned = "analytics.retention.pruned";

    /// <summary>
    /// A notification-retention sweep. Set-based, so there is no before-image: one record per
    /// pass carrying the cutoff and the row counts, not one per deleted row.
    /// </summary>
    public const string NotificationsPruned = "notifications.retention.pruned";

    /// <summary>
    /// Somebody took a copy of the log out of the platform. Recorded in the log itself, which
    /// is the only place it can be: an export leaves no other trace, and the first question
    /// after a leak is who had the data.
    /// </summary>
    public const string AuditExported = "audit.exported";

    /// <summary>A record was opened in full — the diff and metadata, not just the list row.</summary>
    public const string AuditRead = "audit.read";

    /// <summary>
    /// A month of history reached its retention limit and its partition was dropped. Recorded
    /// in the log itself: history disappearing is an event, and the only other account of it
    /// is a log line nobody keeps for four hundred days.
    /// </summary>
    public const string AuditPartitionDropped = "audit.partition.dropped";

    // ---- email ----
    public const string EmailQueued = "email.queued";
    public const string EmailSent = "email.sent";
    public const string EmailFailed = "email.failed";

    /// <summary>Prefix for the generic record the middleware emits when an endpoint declares no action.</summary>
    public const string HttpPrefix = "http.";

    /// <summary>Builds the generic fallback key, e.g. <c>http.post./api/admin/sites</c>.</summary>
    public static string ForRequest(string method, string? routePattern) =>
        $"{HttpPrefix}{method.ToLowerInvariant()}.{routePattern ?? "unknown"}";

    /// <summary>
    /// Prefix for records the EF layer produces for work nobody named — a worker's save, a
    /// consumer's, a plugin's. Distinct from the semantic keys above so the read plane can
    /// separate "someone did this" from "this table changed", which is the difference between
    /// a log an operator reads and one they filter out.
    /// </summary>
    public const string DataPrefix = "data.";

    /// <summary>Builds the unnamed-change key, e.g. <c>data.site.created</c>.</summary>
    public static string ForChange(string resourceType, string verb) => $"{DataPrefix}{resourceType}.{verb}";

    /// <summary>A save too large to enumerate; carries per-type counts instead of a diff.</summary>
    public const string BulkSaved = "data.bulk.saved";

    /// <summary>An <c>ExecuteUpdate</c> nobody described. Carries the table and a row count.</summary>
    public const string BulkUpdated = "data.bulk.updated";

    /// <summary>An <c>ExecuteDelete</c> nobody described. Carries the table and a row count.</summary>
    public const string BulkDeleted = "data.bulk.deleted";

    /// <summary>More unnamed changes in one save than a single record enumerates.</summary>
    public const string ChangesTruncated = "data.saved";

    // ---- platform console ----
    //
    // Written with TenantId = Guid.Empty (AuditEntry.Platform()): these are acts on the
    // platform itself, not inside any tenant. Until the console existed nothing READ those
    // rows; the platform-scope audit reader is what makes them worth writing.

    public const string PlatformUserLocked = "platform.user.locked";
    public const string PlatformUserUnlocked = "platform.user.unlocked";
    public const string PlatformUserPasswordReset = "platform.user.password.reset";
    public const string PlatformUserEmailConfirmed = "platform.user.email.confirmed";

    /// <summary>
    /// A global role granted or revoked. Kept distinct from the lock/unlock actions because
    /// this is the one that can create another SuperAdmin — the single most consequential
    /// thing the console can do, and the first row anyone reviewing a compromise looks for.
    /// </summary>
    public const string PlatformRoleGranted = "platform.user.role.granted";
    public const string PlatformRoleRevoked = "platform.user.role.revoked";

    /// <summary>Which global role holds which console permission.</summary>
    public const string PlatformPermissionsChanged = "platform.role.permissions.changed";

    public const string PlatformTenantSuspended = "platform.tenant.suspended";
    public const string PlatformTenantResumed = "platform.tenant.resumed";

    /// <summary>
    /// Irreversible deletion from a telemetry store. Recorded BEFORE the delete runs, not
    /// after: an action whose record is written only on success is invisible in exactly the
    /// case that matters most.
    /// </summary>
    public const string PlatformLogsPurged = "platform.logs.purged";

    /// <summary>Plugin-contributed action key, mirroring PlatformPermissions.ForPlugin.</summary>
    public static string ForPlugin(string pluginId, string action) => $"plugin.{pluginId}.{action}";

    private static readonly ConcurrentDictionary<string, string> Registered = new(StringComparer.Ordinal);

    /// <summary>
    /// Registers a human-readable label for an action key so the audit UI can render it.
    /// Called by plugins at startup; last registration wins. Never required for recording.
    /// </summary>
    public static void Register(string action, string label) => Registered[action] = label;

    public static string? LabelFor(string action) =>
        Registered.TryGetValue(action, out var label) ? label : null;

    public static IReadOnlyDictionary<string, string> RegisteredLabels => Registered;
}
