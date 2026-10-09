using System.Net.Http.Headers;
using System.Text;
using Dcms.Shared.Audit.Http;
using OpenIddict.Abstractions;

namespace Dcms.Identity.Endpoints;

/// <summary>
/// Lets the edge ask whether a login has ended, so the sessions it holds on its own cookie —
/// Grafana, Forgejo, the admin console — end with the login instead of outliving it.
///
/// <para><b>Why the edge has to ask.</b> Signing out ends the login here: identity's cookie, the
/// token endpoint, the browser's account list. But the edge's cookie on the Grafana and git hosts
/// carries only claims, no token to fail a refresh with, and lasts eight hours. Without this,
/// "Sign out" left both signed in for the rest of the working day.</para>
///
/// <para>Authenticated as the <c>dcms-edge</c> client with its own secret, over HTTP Basic as
/// for the token endpoint. It answers about one id the caller names; the id is a 128-bit random
/// value taken from a token the edge already holds, so the answer tells nobody anything they
/// could not already act on.</para>
/// </summary>
public static class EdgeLoginSessionEndpoints
{
    public static IEndpointRouteBuilder MapEdgeLoginSessionEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/edge/login-sessions/{id}", async (
            string id, HttpContext http, IOpenIddictApplicationManager applications,
            BrowserAccounts accounts, CancellationToken ct) =>
        {
            if (!await IsEdgeAsync(http, applications, ct))
            {
                return Results.Unauthorized();
            }
            return Results.Ok(new { ended = await accounts.HasEndedAsync(id, ct) });
        }).AuditExempt("A read the edge makes about once a minute per signed-in browser; the "
                       + "sign-out that changes its answer is the record worth having.");
        return app;
    }

    private static async Task<bool> IsEdgeAsync(
        HttpContext http, IOpenIddictApplicationManager applications, CancellationToken ct)
    {
        if (!AuthenticationHeaderValue.TryParse(http.Request.Headers.Authorization, out var header)
            || !string.Equals(header.Scheme, "Basic", StringComparison.OrdinalIgnoreCase)
            || header.Parameter is null)
        {
            return false;
        }

        string decoded;
        try
        {
            decoded = Encoding.UTF8.GetString(Convert.FromBase64String(header.Parameter));
        }
        catch (FormatException)
        {
            return false;
        }

        // RFC 6749 §2.3.1: both halves are form-encoded before they are joined.
        var colon = decoded.IndexOf(':');
        if (colon <= 0)
        {
            return false;
        }
        var clientId = Uri.UnescapeDataString(decoded[..colon]);
        var secret = Uri.UnescapeDataString(decoded[(colon + 1)..]);

        return string.Equals(clientId, DcmsOAuth.Clients.Edge, StringComparison.Ordinal)
               && await applications.FindByClientIdAsync(clientId, ct) is { } application
               && await applications.ValidateClientSecretAsync(application, secret, ct);
    }
}
