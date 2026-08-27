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
    }

    public static class Resources
    {
        public const string AdminApi = "dcms-admin-api";
        public const string AiGateway = "dcms-ai-gateway";
    }
}
