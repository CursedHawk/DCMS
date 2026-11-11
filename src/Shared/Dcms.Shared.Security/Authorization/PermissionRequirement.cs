using Microsoft.AspNetCore.Authorization;

namespace Dcms.Shared.Security.Authorization;

/// <param name="anyInstance">
/// For a plugin-wide key (<c>plugin:{id}:{action}</c>) checked where no instance is known: held
/// when the action is held on any instance (<c>plugin:{id}:{slug}:{action}</c>).
/// </param>
public sealed class PermissionRequirement(string permission, bool anyInstance = false) : IAuthorizationRequirement
{
    public string Permission { get; } = permission;

    public bool AnyInstance { get; } = anyInstance;

    /// <summary>Whether a set of held keys satisfies this requirement.</summary>
    public bool IsMetBy(IReadOnlySet<string> held) =>
        held.Contains(Permission) || (AnyInstance && held.Any(k => PlatformPermissions.IsInstanceOf(k, Permission)));
}

/// <summary>
/// Loads the effective permission set for a (tenant, user) pair. Implemented per
/// service over its tenancy data with a Redis cache (key perm:{tenantId}:{userId}).
/// </summary>
public interface IPermissionResolver
{
    Task<IReadOnlySet<string>> GetPermissionsAsync(Guid tenantId, Guid userId, CancellationToken ct = default);
}
