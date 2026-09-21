namespace Dcms.Edge.Auth;

/// <summary>
/// What the edge needs in order to be an OpenID Connect client of the DCMS identity service.
///
/// <para>The edge authenticates a person once, at the boundary, and then tells the service
/// behind it who they are. That removes a whole login implementation from every gated surface —
/// Grafana's <c>generic_oauth</c> block, Forgejo's own account handling — and replaces it with
/// one place that decides.</para>
/// </summary>
public sealed class EdgeAuthOptions
{
    public const string SectionName = "Edge:Auth";

    /// <summary>
    /// The issuer, as a browser sees it: <c>https://admin.highgeek.eu</c>. Identity stamps this
    /// into every token (<c>Identity__Issuer</c> is <c>${PUBLIC_BASE_URL}/</c> in production),
    /// so it is also what token validation compares against — it cannot be an internal address
    /// without every signature check failing.
    /// </summary>
    public string Authority { get; set; } = string.Empty;

    /// <summary>
    /// Where identity actually is, on the compose network. Every back-channel call the handler
    /// makes — discovery, JWKS, the code-for-token exchange — is rewritten onto this address by
    /// <see cref="InternalIdentityHandler"/>.
    ///
    /// <para>Without it those calls would go out to the public hostname and come back in through
    /// this very proxy, which depends on the host NAT hairpinning its own published port. That
    /// is a thing that works until it doesn't, and when it stops, sign-in stops with it. Grafana
    /// already splits its <c>auth_url</c> from its <c>token_url</c> for the same reason.</para>
    /// </summary>
    public string InternalAuthority { get; set; } = "http://identity:8080";

    public string ClientId { get; set; } = "dcms-edge";

    /// <summary>
    /// From Vault (<c>secret/dcms/edge</c>). <b>Empty disables edge authentication entirely</b>
    /// — the gated routes lose their policies and Grafana and Forgejo fall back to their own
    /// login screens. That is a deliberate open-loop rather than a hard failure: this is the
    /// public ingress, and refusing to start because a secret is missing would take every
    /// tenant's site offline over a setting that only affects two operator consoles.
    /// </summary>
    public string ClientSecret { get; set; } = string.Empty;

    /// <summary>True once there is a secret to be a confidential client with.</summary>
    public bool Enabled => !string.IsNullOrWhiteSpace(ClientSecret)
                           && !string.IsNullOrWhiteSpace(Authority);

    /// <summary>
    /// The edge's own session cookie. Deliberately host-scoped — no Domain attribute — so the
    /// Grafana host and the git host each hold their own.
    ///
    /// <para>A cookie scoped to <c>.highgeek.eu</c> would give one sign-in for both, and would
    /// also be attached to every request for a managed tenant subdomain under
    /// <c>dcms.highgeek.eu</c> — where the edge forwards it to site-host, putting an operator's
    /// session cookie inside a tenant-facing service. Host scoping costs a silent redirect
    /// through identity on the second host, and no second prompt: identity's own cookie is
    /// already there.</para>
    /// </summary>
    public string CookieName { get; set; } = "dcms.edge";

    public int SessionHours { get; set; } = 8;

    /// <summary>
    /// Whether the edge acts as the admin console's BFF: holding its API tokens server-side and
    /// attaching the bearer on the way through (ADR 0014).
    ///
    /// <para><b>Off by default, and that is the phase-1 rollout rather than timidity.</b>
    /// Turning it on makes the edge request <c>dcms.admin</c> and <c>offline_access</c> at
    /// sign-in, which OpenIddict refuses until identity has seeded the matching scope
    /// permission on the <c>dcms-edge</c> client. Both ship in one push, but the edge does not
    /// wait on identity in <c>depends_on</c>, so a rolling deploy can start the edge first — and
    /// the symptom of losing that race is operators unable to sign in to Grafana. Shipping the
    /// code off and flipping this afterwards removes the ordering question entirely, and leaves
    /// a kill switch on the public ingress that needs no revert.</para>
    ///
    /// <para>Even on, it only acts on requests that arrive with no <c>Authorization</c> header;
    /// the console sends one until its own phase. So this being true is not yet the cutover.</para>
    /// </summary>
    public bool Bff { get; set; }

    /// <summary>True once the BFF is both wanted and possible.</summary>
    public bool BffEnabled => Bff && Enabled;

    /// <summary>
    /// The scope naming the resource the console calls. One scope covers both destinations:
    /// content-api validates the audience <c>dcms-admin-api</c> too
    /// (<c>Dcms.ContentApi/appsettings.json</c>), so the chat hub accepts the same token
    /// admin-api does.
    /// </summary>
    public string ApiScope { get; set; } = "dcms.admin";

    /// <summary>
    /// The readable half of the double-submit CSRF pair. Not a credential on its own — it is
    /// only good presented alongside the HttpOnly session cookie, and it is bound to that
    /// session. See <see cref="BffGuard"/>.
    /// </summary>
    public string CsrfCookieName { get; set; } = "dcms.csrf";
}
