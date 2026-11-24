using System.Security.Claims;
using Microsoft.AspNetCore.Http;

namespace Dcms.Shared.Security.Agents;

/// <summary>
/// A desktop agent's token (ADR 0032): a person's authority, narrowed to one workspace, some
/// environments and, optionally, reads. admin-api builds the principal from the token's row; these
/// are the claims it carries, and the one place that reads them.
/// </summary>
public static class AgentClaims
{
    /// <summary>What every agent token starts with: how the edge and admin-api tell it from a JWT.</summary>
    public const string TokenPrefix = "dcms_agt_";

    /// <summary>The <c>client_id</c> an agent principal carries, as identity's tokens carry theirs.</summary>
    public const string ClientId = "dcms-agent";

    public const string Grant = "dcms_grant";
    public const string Name = "dcms_agent";
    public const string TenantId = "dcms_tenant";
    public const string Environment = "dcms_env";
    public const string ReadOnly = "dcms_ro";

    public static bool IsAgent(this ClaimsPrincipal? user) => user?.HasClaim(c => c.Type == Grant) == true;

    public static bool IsReadOnlyAgent(this ClaimsPrincipal? user) => user.IsAgent() && user!.HasClaim(ReadOnly, "true");

    /// <summary>Whether a bearer header carries an agent token rather than a JWT.</summary>
    public static bool Carries(string? authorization) =>
        authorization is not null && authorization.StartsWith("Bearer " + TokenPrefix, StringComparison.Ordinal);
}

/// <summary>
/// Whether a desktop agent may make a request (ADR 0032), from its token's claims and the route's
/// CLI metadata alone: the decision admin-api's scope middleware enforces.
/// </summary>
public static class AgentScope
{
    /// <summary>Why the agent may not, or null when it may.</summary>
    /// <param name="environment">The environment the request names; production when it names none.</param>
    public static string? Refusal(CliOperationMetadata? cli, ClaimsPrincipal user, Guid? tenantId, string environment, string method)
    {
        if (cli is null) return "This is not offered to agents.";
        if (cli.HumanOnly) return "Only a person may do this, in the console or with their own dcms sign-in.";
        if (tenantId is null || user.FindFirst(AgentClaims.TenantId)?.Value != tenantId.ToString())
        {
            return "This agent token is for another workspace. Name its workspace in X-Dcms-Tenant.";
        }
        var allowed = user.FindAll(AgentClaims.Environment).Select(c => c.Value).ToList();
        if (allowed.Count > 0 && !allowed.Contains(environment, StringComparer.Ordinal))
        {
            return $"This agent token does not reach the {environment} environment.";
        }
        if (allowed.Count > 0 && cli.CrossEnvironment)
        {
            return "This reaches beyond one environment; an agent token limited to some environments cannot do it.";
        }
        var reads = HttpMethods.IsGet(method) || HttpMethods.IsHead(method);
        if (user.IsReadOnlyAgent() && !reads && cli.Risk is not (CliRisk.Read or CliRisk.PerOperation))
        {
            return "This agent token may only read.";
        }
        return null;
    }
}
