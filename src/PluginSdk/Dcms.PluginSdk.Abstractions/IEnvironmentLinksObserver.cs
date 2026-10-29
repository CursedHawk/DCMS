namespace Dcms.PluginSdk.Abstractions;

/// <summary>
/// Told, in admin-api, when a link of an environment has been made or removed (ADR 0028): by hand
/// or by a deployment, after it is saved. For what follows from a link and would otherwise wait
/// for its next pass -- e.g. User Auth's sign-in rows, which a <c>users</c> link points at another
/// realm.
/// </summary>
public interface IEnvironmentLinksObserver
{
    /// <param name="environmentId">The environment the link belongs to: the one using the other.</param>
    /// <param name="targetEnvironmentId">The environment it names.</param>
    /// <param name="kind"><c>app</c>, <c>users</c>, <c>contracts</c> or <c>tables</c>.</param>
    Task LinkChangedAsync(Guid tenantId, Guid environmentId, Guid targetEnvironmentId, string kind, CancellationToken ct);
}
