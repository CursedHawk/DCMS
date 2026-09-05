using Microsoft.Extensions.Logging;
using VaultSharp.Core;

namespace Dcms.Shared.Vault;

/// <summary>
/// Runs a Vault call and, if Vault rejects the token, mints a fresh one and runs it again.
///
/// <para><b>Why this has to exist.</b> A service authenticates with an AppRole and VaultSharp
/// logs in once, lazily, then caches that token for the life of the client — and the client is
/// a singleton, so that means the life of the process. The issued token has
/// <c>token_ttl=1h</c> / <c>token_max_ttl=4h</c> (see <c>infra/vault/apply.sh</c>), which is
/// deliberate: a short-lived token is most of the point of moving off the shared static one.
/// Nothing renewed it, so every Vault call a service made more than an hour after it started
/// failed with 403 <c>permission denied / invalid token</c>, permanently, until someone
/// restarted the container.</para>
///
/// <para>That was invisible for a long time because most Transit callers are on paths a human
/// exercises minutes after a deploy, or that fall back to something. The edge is the one that
/// made it plain: it decrypts a TLS private key inside the handshake, so an hour after every
/// deploy, every hostname not already in its memory cache — which is every tenant site — began
/// aborting the connection with nothing to see in a browser but a failed handshake.</para>
///
/// <para>Reactive rather than a renewal timer, and that is the cheaper correct answer: it needs
/// no background loop, and it covers the cases a timer does not — a token revoked early, a
/// Vault restarted from a Transit-sealed state, a process that sat idle past its max TTL. The
/// AppRole's secret id is reusable and never expires (<c>secret_id_num_uses=0</c>,
/// <c>secret_id_ttl=0</c>), so logging in again always works.</para>
/// </summary>
public static class VaultTokenRefresh
{
    /// <summary>
    /// Executes <paramref name="operation"/>, retrying it exactly once after
    /// <paramref name="resetToken"/> if Vault answers 403.
    ///
    /// <para>Once, and only on 403. Vault returns the same 403 for "your token expired" and
    /// "your policy does not allow that", and the two are not distinguishable from the response
    /// — so a genuine policy denial costs one extra login and one extra call before it surfaces
    /// unchanged. That is the right trade against the alternative of parsing an error string.
    /// The retry never loops: a second 403 propagates.</para>
    ///
    /// <para>Concurrent callers may each reset and log in, which costs a few extra logins during
    /// the one call in which a token expires. It is not a correctness problem — a discarded
    /// token has already served its request — and serialising it would put a lock in front of
    /// every Transit call to save nothing measurable.</para>
    /// </summary>
    public static async Task<T> ExecuteAsync<T>(
        Func<Task<T>> operation, Action resetToken, ILogger logger, string what)
    {
        try
        {
            return await operation();
        }
        catch (VaultApiException ex) when (ex.StatusCode == 403)
        {
            logger.LogWarning(
                "Vault rejected this service's token ({Status}) trying to {What}; re-authenticating "
                + "and retrying once. If this repeats on every call it is the policy, not the token.",
                ex.StatusCode, what);

            resetToken();
            return await operation();
        }
    }
}
