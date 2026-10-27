namespace Dcms.Shared.Data.Tenancy;

/// <summary>
/// One environment of a tenant (ADR 0028): a stage of its pipeline, a feature branched from one,
/// or a service the others use. What it scopes carries its <see cref="TenantEntity.Id"/> as
/// <see cref="EnvironmentEntity.EnvironmentId"/>; what the workspace shares does not.
///
/// <para>Every tenant has its production environment, whose id is the tenant id, so every row
/// written before environments existed is production's already.</para>
/// </summary>
public sealed class TenantEnvironment : TenantEntity
{
    public const string Production = "production";

    /// <summary>Unique within the tenant; what <c>X-Dcms-Environment</c> names.</summary>
    public string Name { get; set; } = Production;

    /// <summary>Position in the pipeline, lowest first.</summary>
    public int Order { get; set; }

    public EnvironmentPurpose Purpose { get; set; } = EnvironmentPurpose.Stage;

    /// <summary>The environments allowed to deploy into this one.</summary>
    public List<Guid> PromotesFrom { get; set; } = [];

    /// <summary>What a feature environment was branched from.</summary>
    public Guid? ForkedFrom { get; set; }

    /// <summary>Deploying from here offers only the migrations cut here, not those inherited.</summary>
    public bool MergesOwnChangesOnly { get; set; }

    /// <summary>A feature environment deleted once everything it cut is deployed.</summary>
    public bool DeleteWhenMerged { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    public bool IsProduction => Id == TenantId;
}

public enum EnvironmentPurpose
{
    /// <summary>A stage of the pipeline: development, test, staging, production.</summary>
    Stage,
    /// <summary>Branched from another environment to build one feature, and merged back.</summary>
    Feature,
    /// <summary>A supporting service the other environments use; outside the pipeline.</summary>
    Service,
}
