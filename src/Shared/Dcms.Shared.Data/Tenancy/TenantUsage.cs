using Dcms.Shared.Audit.Redaction;

namespace Dcms.Shared.Data.Tenancy;

/// <summary>
/// The latest measurement of everything a tenant stores: database rows, media files, site
/// builds, plugin files and git repositories, broken down by category. One row per tenant,
/// replaced by each measurement (<c>TenantUsageMeasurer</c> in admin-api).
///
/// <para>Report only. The storage cap (<see cref="Tenant.StorageQuotaBytes"/>) still counts media
/// files alone; this is what a tenant costs, not what it is held to.</para>
///
/// <para>Not audited: a derived measurement nobody edits, rewritten for every tenant once a day.
/// Captured, it was one <c>data.tenancy.tenant_usage.*</c> record per tenant per day.</para>
/// </summary>
[AuditIgnore]
public sealed class TenantUsage
{
    public Guid TenantId { get; set; }
    public DateTimeOffset MeasuredAt { get; set; }
    public long TotalBytes { get; set; }

    /// <summary>
    /// <c>[{ key, bytes, items: [{ key, label, kind, bytes, rows? }] }]</c>, both levels sorted by
    /// bytes descending. Served to the admin and platform SPAs as is.
    /// </summary>
    public string CategoriesJson { get; set; } = "[]";
}
