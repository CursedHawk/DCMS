using System.Security.Claims;
using Dcms.Shared.Kernel.Abstractions;
using Microsoft.AspNetCore.Http;

namespace Dcms.Shared.Audit;

/// <summary>
/// Resolves the acting principal from the request's claims.
///
/// Reads the literal <c>sub</c> / <c>email</c> / <c>name</c> / <c>role</c> claim names
/// because the resource servers set <c>MapInboundClaims = false</c> — the long WS-Fed URIs
/// are never present.
///
/// The <c>sub</c> of a client-credentials token is the <c>client_id</c> string, not a Guid,
/// so a machine caller lands on <see cref="ActorKind.ServiceClient"/> with a null
/// <see cref="Id"/> and its identity in <see cref="Key"/>. That is the case the old
/// user-only accessor could not express at all.
/// </summary>
public sealed class HttpCurrentActor(IHttpContextAccessor accessor) : ICurrentActor
{
    private ClaimsPrincipal? Principal => accessor.HttpContext?.User;

    private bool Authenticated => Principal?.Identity?.IsAuthenticated ?? false;

    public ActorKind Kind
    {
        get
        {
            if (!Authenticated)
            {
                return ActorKind.Anonymous;
            }
            // A visitor token is issued by content-api for one tenant's site and its audience
            // says so; platform users come from the OpenIddict server.
            if (Principal!.HasClaim(c => c.Type == "aud" && c.Value.StartsWith("dcms.site:", StringComparison.Ordinal)))
            {
                return ActorKind.Visitor;
            }
            return Id is null ? ActorKind.ServiceClient : ActorKind.User;
        }
    }

    public Guid? Id =>
        Guid.TryParse(Principal?.FindFirstValue("sub"), out var id) ? id : null;

    public string? Key => Id is null ? Principal?.FindFirstValue("sub") : null;

    public string? Display =>
        Principal?.FindFirstValue("email") ?? Principal?.FindFirstValue("name");

    public bool IsSuperAdmin =>
        Principal?.FindAll("role").Any(c => c.Value == "SuperAdmin") ?? false;

    public Guid? OnBehalfOf =>
        Guid.TryParse(Principal?.FindFirstValue("act_sub"), out var id) ? id : null;
}

/// <summary>
/// The actor for a process that has no request: workers, schedulers, consumers.
/// Named after the service so a record says "site-builder did this" rather than leaving the
/// actor blank, which would read as "unknown" when in fact it is precisely known.
/// </summary>
public sealed class SystemCurrentActor(AuditServiceIdentity identity) : ICurrentActor
{
    public ActorKind Kind => ActorKind.System;
    public Guid? Id => null;
    public string? Key => identity.ServiceName;
    public string? Display => identity.ServiceName;
    public bool IsSuperAdmin => false;
    public Guid? OnBehalfOf => null;
}
