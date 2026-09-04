namespace Dcms.Shared.Kernel.Abstractions;

/// <summary>
/// Ambient tenant for the current request or message. Populated by Finbuckle middleware
/// on HTTP paths and by message envelope metadata on NATS consumers.
/// </summary>
public interface ITenantContext
{
    Guid? TenantId { get; }
    string? TenantSlug { get; }
    bool HasTenant => TenantId is not null;

    /// <summary>
    /// The resolved tenant is suspended.
    ///
    /// <para>Defaults to <c>false</c> so the implementations with no tenant row to read — the
    /// null context used by workers, and message-envelope contexts — are unaffected. Only the
    /// HTTP paths, where Finbuckle has already loaded the tenant, answer this meaningfully.
    /// A default of <c>false</c> is the right direction to be wrong in: it fails open on a
    /// path that never had a status to check, rather than silently suspending every worker.</para>
    /// </summary>
    bool IsSuspended => false;
}
