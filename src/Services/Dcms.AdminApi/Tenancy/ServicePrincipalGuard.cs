using Dcms.Shared.Audit;
using Microsoft.AspNetCore.Authorization;

namespace Dcms.AdminApi.Tenancy;

/// <summary>
/// Marks an endpoint another DCMS service is allowed to call with a client-credentials token.
///
/// <para>Rare and deliberate, like <see cref="AllowNonMemberTenantAttribute"/>: the bar is
/// "this endpoint exists to serve another service, and holds nothing a user's own permissions
/// would otherwise gate".</para>
/// </summary>
[AttributeUsage(AttributeTargets.Method | AttributeTargets.Class)]
public sealed class AllowServicePrincipalAttribute(string scope, string reason) : Attribute
{
    /// <summary>The OAuth scope the calling service must hold. Checked, not merely documented.</summary>
    public string Scope { get; } = scope;

    /// <summary>Why a service may call this. Recorded here so the exemption has to be argued for.</summary>
    public string Reason { get; } = reason;
}

/// <summary>
/// Refuses a service token everywhere it was not explicitly invited.
///
/// <para><b>Why this is needed at all.</b> A client-credentials token's <c>sub</c> is the
/// client id, not a user id — so <see cref="CurrentUser"/> reports no user, and
/// <see cref="TenantMembershipMiddleware"/> waves the request through precisely because it
/// looks like an anonymous request that the authorization stack will deal with. It then meets
/// an endpoint guarded by a bare <c>RequireAuthorization()</c>, which asks only for an
/// authenticated caller — and a service token is one. Every such endpoint on the admin plane
/// would be open to any service holding a token admin-api accepts.</para>
///
/// <para>That was harmless while nothing but the admin SPA had an admin-api audience. It stops
/// being harmless the moment content-api — the public-facing service — is granted one so it
/// can read Instagram stories. Closing it here, once, is what makes that grant safe: a service
/// token now reaches exactly the endpoints that named its scope, and nothing else.</para>
///
/// <para>Runs after authentication and before authorization, beside the membership check,
/// because both answer the same question about a caller the endpoint itself cannot see.</para>
/// </summary>
public sealed class ServicePrincipalGuard(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext context, CurrentUser me)
    {
        // Anonymous requests are the authorization stack's business; a real user is the
        // membership middleware's. Only a token with no user behind it concerns this one.
        if (context.User.Identity?.IsAuthenticated != true || me.UserId is not null)
        {
            await next(context);
            return;
        }

        var endpoint = context.GetEndpoint();
        var allowance = endpoint?.Metadata.GetMetadata<AllowServicePrincipalAttribute>();

        // An endpoint that is anonymous anyway (the OAuth callback, the webhooks) has its own
        // credential check and never wanted a user in the first place.
        var anonymous = endpoint?.Metadata.GetMetadata<IAllowAnonymous>() is not null;

        if (allowance is not null && HasScope(context, allowance.Scope))
        {
            await next(context);
            return;
        }
        if (allowance is null && anonymous)
        {
            await next(context);
            return;
        }

        var audit = context.RequestServices.GetRequiredService<IAuditRecorder>();
        audit.Record(AuditActions.PermissionDenied)
            .Platform()
            .As(AuditCategory.Security, AuditSeverity.Warning)
            .With("path", context.Request.Path.Value)
            .With("client", context.User.FindFirst("sub")?.Value)
            .Denied(allowance is null
                ? "service token on an endpoint that does not accept service callers"
                : $"service token without the required scope '{allowance.Scope}'");

        // 403 rather than 401: the token is valid and was understood. Re-authenticating would
        // produce the same token and the same answer.
        context.Response.StatusCode = StatusCodes.Status403Forbidden;
    }

    /// <summary>
    /// OpenIddict writes granted scopes as a space-delimited <c>scope</c> claim, but a JWT may
    /// carry them as repeated claims or under <c>scp</c> depending on the issuer. Reading all
    /// three shapes costs nothing and avoids a guard that silently passes nobody.
    /// </summary>
    private static bool HasScope(HttpContext context, string required) =>
        context.User.FindAll("scope").Concat(context.User.FindAll("scp"))
            .SelectMany(c => c.Value.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            .Any(s => string.Equals(s, required, StringComparison.Ordinal));
}

public static class ServicePrincipalGuardExtensions
{
    /// <summary>
    /// Confines client-credentials callers to endpoints that opted in. Register after
    /// <c>UseAuthentication</c> and before <c>UseAuthorization</c>.
    /// </summary>
    public static IApplicationBuilder UseServicePrincipalGuard(this IApplicationBuilder app) =>
        app.UseMiddleware<ServicePrincipalGuard>();

    /// <summary>
    /// Lets another DCMS service call this endpoint with a client-credentials token carrying
    /// <paramref name="scope"/>. See <see cref="AllowServicePrincipalAttribute"/>.
    /// </summary>
    public static TBuilder AllowServicePrincipal<TBuilder>(this TBuilder builder, string scope, string reason)
        where TBuilder : IEndpointConventionBuilder
    {
        builder.WithMetadata(new AllowServicePrincipalAttribute(scope, reason));
        return builder;
    }
}
