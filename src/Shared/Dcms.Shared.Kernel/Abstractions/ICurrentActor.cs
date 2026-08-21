namespace Dcms.Shared.Kernel.Abstractions;

/// <summary>
/// What kind of thing performed an action. Audit records need this because the
/// platform has several non-human callers whose identity is not a user id:
/// client-credentials tokens carry the client_id as their subject, the Forgejo
/// push webhook is authenticated by HMAC with no subject at all, and the NATS
/// consumers act on their own behalf.
/// </summary>
public enum ActorKind
{
    /// <summary>No credentials were presented (public delivery traffic).</summary>
    Anonymous,

    /// <summary>A platform user, identified by the "sub" claim as a Guid.</summary>
    User,

    /// <summary>An OAuth client-credentials principal; <see cref="ICurrentActor.Key"/> is the client_id.</summary>
    ServiceClient,

    /// <summary>A site visitor holding a visitor token; scoped to one tenant's site.</summary>
    Visitor,

    /// <summary>An inbound webhook verified by a shared secret rather than a principal.</summary>
    Webhook,

    /// <summary>A background worker or scheduler acting with no originating request.</summary>
    System,

    /// <summary>Plugin-initiated work; <see cref="ICurrentActor.Key"/> is the plugin id.</summary>
    Plugin,
}

/// <summary>
/// Ambient "who is acting" for the current request or message, the actor counterpart
/// to <see cref="ITenantContext"/>. Populated from claims on HTTP paths and from
/// message metadata (or a fixed system identity) on NATS consumers.
///
/// Deliberately not a user-only abstraction: <see cref="Id"/> is null for every actor
/// whose identity is not a Guid, and <see cref="Key"/> carries that identity instead.
/// Audit records must be able to say "the git webhook did this" without inventing a user.
/// </summary>
public interface ICurrentActor
{
    ActorKind Kind { get; }

    /// <summary>User or visitor id, when the subject is a Guid. Null otherwise.</summary>
    Guid? Id { get; }

    /// <summary>Non-Guid identity: client_id, service name, plugin id, repo full name.</summary>
    string? Key { get; }

    /// <summary>Email or display name, captured so the log survives the account being deleted.</summary>
    string? Display { get; }

    bool IsSuperAdmin { get; }

    /// <summary>Set when a platform operator is acting on a user's behalf.</summary>
    Guid? OnBehalfOf { get; }

    bool IsAuthenticated => Kind is not ActorKind.Anonymous;
}
