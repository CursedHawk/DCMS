namespace Dcms.Identity;

/// <summary>
/// Stable client ids, scopes and resource (audience) names shared between the
/// authorization server seeding and the resource servers' token validation.
/// </summary>
public static class DcmsOAuth
{
    public static class Clients
    {
        public const string AdminSpa = "dcms-admin-spa";          // public, code+PKCE
        public const string AdminApiService = "dcms-admin-api";    // confidential, client credentials
        /// <summary>
        /// RETIRED. Grafana no longer speaks OIDC: the edge authenticates and passes
        /// <c>X-WEBAUTH-USER</c>. Kept as a constant because the seeder deletes any surviving
        /// row by this id — a client whose role mapping granted a platform-wide read is not
        /// something to leave lying in the database because nothing points at it any more.
        /// </summary>
        public const string Grafana = "dcms-grafana";
        public const string PlatformSpa = "dcms-platform-spa";     // public, code+PKCE

        /// <summary>
        /// The edge (Dcms.Edge). Confidential, code+PKCE. It signs an operator in once at the
        /// boundary and tells Grafana and Forgejo who they are, which is what retired
        /// <see cref="Grafana"/> above.
        /// </summary>
        public const string Edge = "dcms-edge";

        /// <summary>
        /// platform-api → admin-api, for the console's own reads and writes.
        ///
        /// <para>A separate confidential client rather than a reuse of
        /// <see cref="AdminApiService"/>, which content-api already holds the secret for.
        /// Sharing it would hand platform-api <c>dcms.ai</c> and <c>dcms.social</c> as well —
        /// and <c>infra/vault/policies/dcms-platform-api.hcl</c> argues the opposite direction
        /// for exactly this service.</para>
        /// </summary>
        public const string PlatformApiService = "dcms-platform-api-service";
    }

    /// <summary>
    /// Every constant here must ALSO appear in <c>options.RegisterScopes(...)</c> in
    /// Program.cs, and be seeded by <c>IdentitySeeder.SeedScopesAsync</c>. Three places, and
    /// missing the middle one is silent: the scope exists in the database and is absent from
    /// discovery, so sign-in fails for the SPA that asks for it.
    /// </summary>
    public static class Scopes
    {
        public const string Admin = "dcms.admin";   // admin-api resource scope
        public const string Ai = "dcms.ai";         // ai-gateway resource scope

        /// <summary>
        /// content-api → admin-api, for Instagram stories. Maps to the admin-api resource like
        /// <see cref="Admin"/>, but is deliberately its own scope: content-api is the
        /// internet-facing service, and the endpoint it needs is one narrow read. Reusing
        /// dcms.admin would have handed it a token indistinguishable from the SPA's.
        /// </summary>
        public const string Social = "dcms.social";

        /// <summary>
        /// The platform console → platform-api. Its own resource, so a console token is not
        /// interchangeable with an admin SPA token: the console holds the platform's delete
        /// buttons, and a scope that also validated against admin-api would make "this token
        /// may purge a telemetry store" and "this token may edit content" the same claim.
        /// </summary>
        public const string Platform = "dcms.platform";

        /// <summary>
        /// platform-api → admin-api, on behalf of an operator.
        ///
        /// <para>The admin-api resource, like <see cref="Admin"/> and <see cref="Social"/>, and
        /// its own scope for the same reason as Social: the endpoints it opens are a narrow,
        /// named set — the console's certificates, its notifications, tenant lifecycle and the
        /// analytics prune — and <c>ServicePrincipalGuard</c> confines the token to exactly the
        /// endpoints that named this scope. Reusing <c>dcms.admin</c> would have given
        /// platform-api a token indistinguishable from the admin SPA's.</para>
        /// </summary>
        public const string Console = "dcms.console";
    }

    public static class Resources
    {
        public const string AdminApi = "dcms-admin-api";
        public const string AiGateway = "dcms-ai-gateway";
        public const string PlatformApi = "dcms-platform-api";
    }
}
