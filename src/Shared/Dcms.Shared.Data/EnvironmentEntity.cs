namespace Dcms.Shared.Data;

/// <summary>
/// An entity that belongs to one environment of its tenant (ADR 0028): content, plugins, sites,
/// Dynamic Apps and everything else a workspace builds. Filtered and protected by both columns;
/// production's <see cref="EnvironmentId"/> is the tenant id. What a workspace shares across its
/// environments (members, media, settings) stays a plain <see cref="TenantEntity"/>.
/// </summary>
public abstract class EnvironmentEntity : TenantEntity
{
    public Guid EnvironmentId { get; set; }
}
