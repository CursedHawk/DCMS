using System.Security.Claims;
using System.Text.Encodings.Web;
using Dcms.Shared.Kernel.Abstractions;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Dcms.Plugins.VisitorAuth;

/// <summary>
/// The <c>DcmsVisitor</c> authentication scheme: a bearer visitor token, valid for the request's
/// tenant only (the audience binds it). Never the default scheme — a platform user's token and
/// a visitor's are both bearer tokens, and only an endpoint that asks for a visitor gets this.
///
/// <para>The principal carries <c>aud = dcms.site:{tenant}</c>, which is what
/// <c>HttpCurrentActor</c> reads to report <see cref="ActorKind.Visitor"/>, so the audit log
/// names the visitor on every endpoint that authenticates one.</para>
/// </summary>
public sealed class VisitorAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder,
    VisitorTokenService tokens,
    ITenantContext tenant)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string SchemeName = "DcmsVisitor";

    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var header = Request.Headers.Authorization.ToString();
        if (!header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) || tenant.TenantId is not { } tenantId)
        {
            return AuthenticateResult.NoResult();
        }
        var token = header["Bearer ".Length..].Trim();
        var visitorId = await tokens.ValidateAccessTokenAsync(token, tenantId);
        if (visitorId is not { } id)
        {
            return AuthenticateResult.Fail("Invalid visitor token.");
        }

        var identity = new ClaimsIdentity(
            [
                new Claim("sub", id.ToString()),
                new Claim("aud", VisitorTokenService.Audience(tenantId)),
            ],
            SchemeName, "sub", "role");
        return AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), SchemeName));
    }
}

public static class VisitorAuthorization
{
    public const string Policy = "DcmsVisitor";

    /// <summary>Only a signed-in visitor of this tenant reaches the handler; everyone else gets 401.</summary>
    public static RouteHandlerBuilder RequireVisitor(this RouteHandlerBuilder builder) =>
        builder.RequireAuthorization(Policy);

    /// <summary>The visitor id of an endpoint guarded by <see cref="RequireVisitor"/>.</summary>
    public static Guid VisitorId(this ClaimsPrincipal user) => Guid.Parse(user.FindFirstValue("sub")!);

    /// <summary>
    /// The signed-in visitor, if any, without requiring one — for endpoints and contracts that
    /// serve anonymous callers too.
    /// </summary>
    public static async Task<Guid?> TryAuthenticateVisitorAsync(this HttpContext http)
    {
        var result = await http.AuthenticateAsync(VisitorAuthenticationHandler.SchemeName);
        return result.Succeeded && Guid.TryParse(result.Principal.FindFirstValue("sub"), out var id) ? id : null;
    }
}
