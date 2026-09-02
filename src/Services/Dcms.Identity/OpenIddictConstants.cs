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
        public const string Grafana = "dcms-grafana";              // confidential, code+PKCE
    }

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
    }

    public static class Resources
    {
        public const string AdminApi = "dcms-admin-api";
        public const string AiGateway = "dcms-ai-gateway";
    }
}
