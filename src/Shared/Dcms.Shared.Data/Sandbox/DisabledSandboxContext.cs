using Dcms.Shared.Kernel.Abstractions;

namespace Dcms.Shared.Data.Sandbox;

/// <summary>
/// The default sandbox context: always live. Registered by the data-layer
/// service extensions so every host resolves an <see cref="ISandboxContext"/>;
/// content-api overrides it with a header-driven one on the delivery plane.
/// </summary>
public sealed class DisabledSandboxContext : ISandboxContext
{
    public static readonly DisabledSandboxContext Instance = new();
    public bool IsSandbox => false;
}
