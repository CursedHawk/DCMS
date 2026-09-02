using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Dcms.IntegrationTests.Tenancy;

/// <summary>
/// Test authentication: builds a principal from request headers (X-Test-Sub,
/// X-Test-Roles, X-Test-Email, X-Test-Scope) so isolation/permission tests can act as
/// different users without minting real OIDC tokens.
///
/// <para>X-Test-Scope stands in for a client-credentials token's granted scopes. A caller
/// whose X-Test-Sub is not a user guid is a service principal, which is exactly what
/// <c>ServicePrincipalGuard</c> exists to confine — so the tests that prove it confines
/// anything need a way to be one.</para>
/// </summary>
public sealed class TestAuthHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder) : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string SchemeName = "Test";

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!Request.Headers.TryGetValue("X-Test-Sub", out var sub) || string.IsNullOrEmpty(sub))
        {
            return Task.FromResult(AuthenticateResult.NoResult());
        }

        var claims = new List<Claim> { new("sub", sub.ToString()) };
        if (Request.Headers.TryGetValue("X-Test-Email", out var email))
        {
            claims.Add(new Claim("email", email.ToString()));
        }
        if (Request.Headers.TryGetValue("X-Test-Roles", out var roles))
        {
            foreach (var role in roles.ToString().Split(',', StringSplitOptions.RemoveEmptyEntries))
            {
                claims.Add(new Claim("role", role.Trim()));
            }
        }

        // Space-delimited, as OpenIddict writes it, so the guard parses a real token's shape.
        if (Request.Headers.TryGetValue("X-Test-Scope", out var scope) && !string.IsNullOrEmpty(scope))
        {
            claims.Add(new Claim("scope", scope.ToString()));
        }

        var identity = new ClaimsIdentity(claims, SchemeName, "name", "role");
        var ticket = new AuthenticationTicket(new ClaimsPrincipal(identity), SchemeName);
        return Task.FromResult(AuthenticateResult.Success(ticket));
    }
}
