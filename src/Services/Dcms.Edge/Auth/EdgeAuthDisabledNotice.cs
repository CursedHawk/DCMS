namespace Dcms.Edge.Auth;

/// <summary>
/// Says, once and clearly, that the edge is not authenticating anybody.
///
/// <para>Edge authentication fails OPEN when <c>Edge:Auth:ClientSecret</c> is empty: no route
/// carries a policy, no identity header is asserted, and Grafana and Forgejo fall back to
/// their own sign-in. That is the right trade — this is the public ingress, and refusing to
/// start over a setting affecting two operator consoles would take every tenant's site offline
/// — but it must not be silent. It was, and it hid for a whole deployment.</para>
///
/// <para>A separate hosted service rather than a line in <c>AddEdgeAuthentication</c> because
/// that runs during configuration, before logging is built: a message written there goes to the
/// bootstrap logger and is easy to miss among startup noise. Here it lands in the same stream
/// as everything else an operator greps.</para>
/// </summary>
public sealed class EdgeAuthDisabledNotice(ILogger<EdgeAuthDisabledNotice> logger) : BackgroundService
{
    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogWarning(
            "SINGLE SIGN-ON IS OFF: Edge:Auth:ClientSecret is empty, so the edge authenticates "
            + "nobody. Grafana and Forgejo are proxied UNGATED and fall back to their own login "
            + "pages -- and a user mirrored from DCMS may have no Forgejo password, so for them "
            + "that page cannot be passed. Set EDGE_OIDC_CLIENT_SECRET in .env; identity seeds "
            + "the dcms-edge client from the same value, so both must be rolled together. "
            + "infra/vault/apply.sh generates one if it is missing.");
        return Task.CompletedTask;
    }
}
