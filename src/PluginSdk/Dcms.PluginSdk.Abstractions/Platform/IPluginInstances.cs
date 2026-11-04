using Dcms.PluginSdk.Abstractions.Contracts;

namespace Dcms.PluginSdk.Abstractions.Platform;

/// <param name="Slug">The app's address in this environment.</param>
public sealed record InstanceEnsure(string Slug, string Name, string? Description = null);

/// <param name="Created">Whether this call installed it.</param>
public sealed record InstanceRef(Guid Id, string Slug, bool Created);

/// <summary>
/// An instance of the calling plugin, installed by the plugin itself: for work the platform does
/// on a workspace's behalf, such as moving Dynamic Apps flows into a Functions app (ADR 0029).
/// Only the caller's own plugin, in the tenant and environment it runs in; never another
/// plugin's, and never an address another app holds.
///
/// <para><b>Available in admin-api only.</b></para>
/// </summary>
[DcmsContract("dcms.instances", 1, Description = "Install an instance of the calling plugin itself (admin plane only).")]
public interface IPluginInstances
{
    /// <summary>The caller's instance at <see cref="InstanceEnsure.Slug"/>, installed when missing.</summary>
    /// <exception cref="ContractConflictException">Another plugin's app has the address, or the caller's is switched off: a person did that.</exception>
    [Operation(OpRisk.Safe)]
    Task<InstanceRef> EnsureOwnAsync(InstanceEnsure input, CancellationToken ct);
}
