namespace Dcms.Edge;

/// <summary>
/// The platform-plane edge configuration: the four hostnames DCMS owns and the
/// internal address of every service behind the edge.
///
/// <para>These are the same values the Caddyfile took from the environment
/// (<c>ADMIN_HOST</c>, <c>PLATFORM_HOST</c>, <c>GRAFANA_DOMAIN</c>, <c>GIT_HOST</c>), and they
/// carry the same defaults for the same reason: dev and production deploy the same image, so a
/// hostname baked in here would make the dev edge answer for production's name.</para>
///
/// <para>Bound from the <c>Edge</c> configuration section. Everything not named here — tenant
/// custom domains — is served by the catch-all route to site-host, which resolves the tenant
/// from the Host header itself.</para>
/// </summary>
public sealed class EdgeOptions
{
    public const string SectionName = "Edge";

    public string AdminHost { get; set; } = "admin.highgeek.eu";
    public string PlatformHost { get; set; } = "platform.highgeek.eu";
    public string GrafanaHost { get; set; } = "grafana.highgeek.eu";
    public string GitHost { get; set; } = "git.highgeek.eu";

    public EdgeUpstreams Upstreams { get; set; } = new();
}

/// <summary>
/// Internal addresses of the services the edge proxies to. Compose DNS names, so the defaults
/// are correct inside the stack and only a local (non-container) run needs to override them.
/// </summary>
public sealed class EdgeUpstreams
{
    public string Identity { get; set; } = "http://identity:8080";
    public string AdminApi { get; set; } = "http://admin-api:8080";
    public string PlatformApi { get; set; } = "http://platform-api:8080";
    public string ContentApi { get; set; } = "http://content-api:8080";
    public string SiteHost { get; set; } = "http://site-host:8080";
    public string AdminSpa { get; set; } = "http://admin-spa:80";
    public string PlatformSpa { get; set; } = "http://platform-spa:80";
    public string Grafana { get; set; } = "http://grafana:3000";
    public string Forgejo { get; set; } = "http://forgejo:3000";
}
