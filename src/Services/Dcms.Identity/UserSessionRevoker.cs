using Dcms.Identity.Domain;
using Microsoft.AspNetCore.Identity;
using OpenIddict.Abstractions;

namespace Dcms.Identity;

/// <summary>
/// Ends a user's live sessions. Revokes every OpenIddict access/refresh token and authorization
/// held for the subject and rotates the ASP.NET Identity security stamp.
///
/// <para>Called when an account is locked or its password changes so those actions actually
/// <b>contain</b> a compromised or terminated account rather than only stopping the next
/// interactive sign-in. Before this, a locked account kept minting access tokens by refreshing
/// for the whole 14-day refresh lifetime, and a password change left the attacker's existing
/// refresh token working. (SEC-05)</para>
/// </summary>
public static class UserSessionRevoker
{
    public static async Task RevokeAllAsync(
        IOpenIddictTokenManager tokens,
        IOpenIddictAuthorizationManager authorizations,
        UserManager<DcmsUser> users,
        DcmsUser user,
        CancellationToken ct = default)
    {
        var subject = await users.GetUserIdAsync(user);

        // Revoke the tokens first: an authorization with no live tokens is inert, but a token
        // whose authorization was revoked is still refused, so tokens are the load-bearing half.
        await foreach (var token in tokens.FindBySubjectAsync(subject, ct))
        {
            await tokens.TryRevokeAsync(token, ct);
        }

        await foreach (var authorization in authorizations.FindBySubjectAsync(subject, ct))
        {
            await authorizations.TryRevokeAsync(authorization, ct);
        }

        // Rotates the stamp so anything keyed on it — notably the interactive Identity cookie's
        // validation — no longer matches, ending the interactive session too.
        await users.UpdateSecurityStampAsync(user);
    }
}
