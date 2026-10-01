using Finbuckle.MultiTenant.Abstractions;

namespace Dcms.Shared.Data.Tenancy;

/// <summary>
/// A tenant organisation. Doubles as the Finbuckle <see cref="ITenantInfo"/>
/// payload: <see cref="Id"/> is the uuid (as string), <see cref="Identifier"/>
/// is the slug. The tenants table is not itself tenant-scoped.
/// </summary>
public sealed class Tenant : ITenantInfo
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string Identifier { get; set; } = string.Empty;
    public string? Name { get; set; }
    public TenantStatus Status { get; set; } = TenantStatus.Active;
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>
    /// How much the tenant's media library may hold, originals and renditions together — the
    /// same total the console's tenant list shows. New uploads are refused once it is reached.
    /// Set per tenant from the platform console.
    /// </summary>
    public long StorageQuotaBytes { get; set; } = DefaultStorageQuotaBytes;

    public const long DefaultStorageQuotaBytes = 5L * 1024 * 1024 * 1024;

    public Guid TenantId => Guid.Parse(Id);
}

public enum TenantStatus
{
    Active,
    Suspended,
}
