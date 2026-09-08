using Dcms.Shared.Audit;
using Dcms.Shared.Kernel.Abstractions;

namespace Dcms.AdminApi.Tenancy;

/// <summary>
/// Who may act on platform-wide state: a SuperAdmin signed in here, or the platform console's
/// own API acting for one.
///
/// <para><b>Why a second answer was needed.</b> Roughly a dozen endpoints on this service are
/// gated by <c>if (!me.IsSuperAdmin) return Results.Forbid();</c> — the certificates the
/// platform holds, the console's bell, tenant suspend and resume, the analytics prune. Every
/// one of them is reached from the platform console, and the console is moving off admin-api
/// as a browser-facing origin: its SPA will call platform-api, which calls this. A
/// client-credentials token carries a client id rather than a user id, so <c>IsSuperAdmin</c>
/// is false for it and would refuse every one of these.</para>
///
/// <para><b>Why that is not a hole.</b> The decision has not moved, it has moved <i>up</i>.
/// platform-api gates each of these on a <c>PlatformConsolePermissions</c> key held by the
/// operator, which is a finer answer than "is a SuperAdmin" — it is what makes ten of the
/// sixteen console permissions mean something for the first time. What reaches here is a caller
/// that has already been checked, and this class asks only whether it is that caller:
/// <see cref="ServicePrincipalGuard"/> has already verified the token carries
/// <see cref="Scope"/> and that the endpoint invited it, so an endpoint without the attribute
/// gets no service caller at all.</para>
///
/// <para>The alternative was a parallel set of internal routes wrapping the same handlers. One
/// authorization answer in one class beats thirteen re-implementations that can drift.</para>
/// </summary>
public sealed class ConsoleCaller(IHttpContextAccessor accessor, CurrentUser me, AuditScope audit)
{
    /// <summary>
    /// The scope <c>dcms-platform-api-service</c> holds, and nothing else does. Deliberately not
    /// the scope content-api was granted: that client's secret is shared with content-api, and
    /// a service on the public delivery plane must not be able to suspend a tenant.
    /// </summary>
    public const string Scope = "dcms.console";

    /// <summary>May this caller act on platform-wide state?</summary>
    public bool Allowed => me.IsSuperAdmin || IsConsoleService;

    /// <summary>
    /// The operator whose own per-user state this acts on — read and dismiss state on the
    /// console's bell, which is keyed on a user id.
    ///
    /// <para>For a service caller that id comes from the propagated actor headers
    /// (<see cref="PropagatedActorMiddleware"/>), which is an assertion by a peer rather than
    /// something authenticated here. Trusting it for this is bounded: the caller is already
    /// trusted to suspend a tenant, so choosing which operator's bell to mark read is strictly
    /// less than what it can already do, and the rows are UI state that nothing else reads.</para>
    /// </summary>
    public Guid RequireOperatorId()
    {
        if (me.UserId is { } signedIn)
        {
            return signedIn;
        }

        if (IsConsoleService && audit.ResolveActor() is { Kind: ActorKind.User, Id: { } propagated })
        {
            return propagated;
        }

        throw new InvalidOperationException(
            "No operator behind this request. A console service call must carry the propagated "
            + "actor headers for any endpoint that touches per-user state.");
    }

    /// <summary>
    /// A client-credentials caller on an endpoint that named <see cref="Scope"/>. The token's
    /// scope itself was checked by <see cref="ServicePrincipalGuard"/> before the request got
    /// here; re-reading the claim would only duplicate that.
    /// </summary>
    private bool IsConsoleService =>
        me.UserId is null
        && accessor.HttpContext is { } context
        && context.User.Identity?.IsAuthenticated == true
        && context.GetEndpoint()?.Metadata.GetMetadata<AllowServicePrincipalAttribute>() is { } allowance
        && allowance.Scope == Scope;
}

public static class ConsoleCallerExtensions
{
    /// <summary>
    /// Lets the platform console's API call this endpoint for an operator it has already
    /// authorized. Pair with a <see cref="ConsoleCaller.Allowed"/> check in the handler.
    /// </summary>
    public static TBuilder AllowConsoleService<TBuilder>(this TBuilder builder, string reason)
        where TBuilder : IEndpointConventionBuilder =>
        builder.AllowServicePrincipal(ConsoleCaller.Scope, reason);
}
