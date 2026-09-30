using Microsoft.AspNetCore.Builder;

namespace Dcms.Shared.Security;

/// <summary>
/// Marks an endpoint another DCMS service is allowed to call with a client-credentials token.
///
/// <para>Rare and deliberate, like admin-api's <c>AllowNonMemberTenant</c>: the bar is
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

/// <summary>Enforced by admin-api's <c>ServicePrincipalGuard</c>; declared here so plugin routes can opt in.</summary>
public static class ServicePrincipalAllowanceExtensions
{
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
