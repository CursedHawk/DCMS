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

    /// <summary>
    /// The workspace this tenant is an environment of: the root tenant's id, the root's own id on
    /// the root itself, and null while the workspace has not enabled environments (ADR 0026).
    /// Every environment is a full tenant; this column is the only thing that groups them.
    /// </summary>
    public Guid? WorkspaceId { get; set; }

    /// <summary>The environment's name within its workspace (<c>production</c> on the root).</summary>
    public string? EnvironmentName { get; set; }

    /// <summary>Position in the pipeline, lowest first; the root is last.</summary>
    public int EnvironmentOrder { get; set; }

    /// <summary>The environments allowed to promote configuration into this one.</summary>
    public List<Guid> PromotesFrom { get; set; } = [];

    public const string ProductionEnvironment = "production";

    public Guid TenantId => Guid.Parse(Id);

    /// <summary>The workspace root's id: <see cref="WorkspaceId"/>, or this tenant when environments are off.</summary>
    public Guid RootId => WorkspaceId ?? TenantId;

    public bool IsEnvironmentRoot => WorkspaceId is null || WorkspaceId == TenantId;
}

public enum TenantStatus
{
    Active,
    Suspended,
}
