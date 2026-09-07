namespace Dcms.Shared.Data.Notifications;

/// <summary>
/// Something the platform itself needs to tell its operators about — a certificate that could
/// not be issued, one about to expire — as opposed to something that happened inside a tenant.
///
/// <para><b>Why not the tenant <see cref="Notification"/> tables.</b> Those hang off
/// <c>TenantEntity</c>: every row carries a <c>TenantId</c>, the query filter compares it to the
/// ambient tenant, and the unique index that provides idempotency is
/// <c>(TenantId, DedupeKey)</c>. A platform notification has no tenant, and the available
/// workarounds are worse than a second pair of tables — a sentinel <c>Guid.Empty</c> tenant
/// would make every one of those mechanisms mean something different depending on the row, in
/// the one place where getting tenant scoping wrong shows a tenant another tenant's business.
/// </para>
///
/// <para><b>There is deliberately no TenantId</b>, which is what keeps these two tables
/// legitimately outside <c>RlsConfigurator.TenantTables</c> rather than missing from it — the
/// same reasoning recorded on the edge's certificate tables. If either ever gains a tenant
/// column it must be registered there, and in <c>AssertRlsCoverage</c>, in the same commit.</para>
///
/// <para><b>The text is not stored.</b> Only <see cref="Kind"/> and <see cref="ParamsJson"/> —
/// the facts. The console renders the sentence, so a sentence can be rewritten without
/// rewriting history, and prose written into a row at raise time cannot go stale against an
/// entity that has since been renamed. The one exception is a certificate authority's own error
/// message, which arrives as a parameter because reproducing it verbatim is the entire value of
/// showing it at all.</para>
/// </summary>
public sealed class PlatformNotification
{
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>Stable discriminator, e.g. <c>certificate.failed</c>. Chooses the sentence and the icon.</summary>
    public string Kind { get; set; } = string.Empty;

    public NotificationSeverity Severity { get; set; } = NotificationSeverity.Info;

    /// <summary>JSON object of facts for the console to render with. Never rendered prose.</summary>
    public string ParamsJson { get; set; } = "{}";

    /// <summary>Console route this points at, e.g. <c>/certificates</c>. Null when there is nowhere to go.</summary>
    public string? LinkPath { get; set; }

    public string? ResourceType { get; set; }
    public Guid? ResourceId { get; set; }

    /// <summary>
    /// Names the underlying <b>fact</b>, and is unique. Raisers insert optimistically and read a
    /// unique violation as "already told them", which is what makes a worker that re-reads a
    /// window of history on every pass safe to run every two minutes.
    ///
    /// <para>Not the id of whatever triggered the pass, for the reason recorded on
    /// <see cref="Notification.DedupeKey"/>: key on the thing the notification is <i>about</i>.
    /// Here that is the attempt row, or the certificate plus the expiry being warned about —
    /// <c>certificate.expiring:{id}:{notAfter}</c> renews its warning when the certificate does,
    /// and says nothing twice about the same one.</para>
    /// </summary>
    public string DedupeKey { get; set; } = string.Empty;

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>
/// One operator's read state for one platform notification, created when they act on it.
///
/// <para><b>Created lazily, which is the opposite of the tenant model</b>, where a recipient row
/// per addressee is materialised at raise time. That is the better design there and impossible
/// here: it needs the audience enumerated up front, and the audience for these is "everyone
/// holding the SuperAdmin global role" — a role that lives in identity, which admin-api does not
/// read. Giving the notification writer a reason to enumerate identity's users, so that a bell
/// can show a number, is a poor trade.</para>
///
/// <para>The cost is that the unread count is an anti-join rather than an indexed count. That is
/// affordable precisely because of what these are: the tenant tables take a row per site
/// publish and per upload, while this one takes a row when a certificate changes state — single
/// figures per week, against tens of thousands.</para>
/// </summary>
public sealed class PlatformNotificationRead
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid NotificationId { get; set; }

    /// <summary>The operator, from the <c>sub</c> claim. Not a foreign key: identity owns users.</summary>
    public Guid UserId { get; set; }

    public DateTimeOffset? ReadAt { get; set; }

    /// <summary>
    /// Set when an operator clears it from their bell. Their own view only — dismissing is not
    /// deleting, and another operator has not seen it yet.
    /// </summary>
    public DateTimeOffset? DismissedAt { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    public PlatformNotification? Notification { get; set; }
}
