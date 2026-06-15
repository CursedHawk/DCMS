using Microsoft.AspNetCore.Authorization;

namespace Dcms.Shared.Security.Authorization;

public sealed class PermissionRequirement(string permission) : IAuthorizationRequirement
{
    public string Permission { get; } = permission;
}

/// <summary>
/// Loads the effective permission set for a (tenant, user) pair. Implemented per
/// service over its tenancy data with a Redis cache (key perm:{tenantId}:{userId}).
/// </summary>
public interface IPermissionResolver
{
    Task<IReadOnlySet<string>> GetPermissionsAsync(Guid tenantId, Guid userId, CancellationToken ct = default);
}
