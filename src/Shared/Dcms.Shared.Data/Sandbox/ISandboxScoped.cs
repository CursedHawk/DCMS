namespace Dcms.Shared.Data;

/// <summary>
/// A tenant entity that can live in the per-tenant preview sandbox. Rows with
/// <see cref="IsSandbox"/> = false are live; sandbox rows are written by preview
/// traffic and are filtered out of live queries (and vice-versa). The active
/// space is chosen per request by <see cref="Kernel.Abstractions.ISandboxContext"/>.
/// </summary>
public interface ISandboxScoped
{
    bool IsSandbox { get; set; }
}
