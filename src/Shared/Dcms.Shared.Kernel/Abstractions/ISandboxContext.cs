namespace Dcms.Shared.Kernel.Abstractions;

/// <summary>
/// Ambient "sandbox" flag for the current request. When true, tenant-scoped
/// writes on sandbox-aware tables land in an isolated, per-tenant sandbox space
/// instead of the live data — used by the site preview so test submissions,
/// visitor sign-ups and chats never touch a tenant's real records. Populated
/// from the <c>X-Dcms-Sandbox</c> header on the preview/delivery plane; false
/// everywhere else (admin, background workers).
/// </summary>
public interface ISandboxContext
{
    bool IsSandbox { get; }
}
