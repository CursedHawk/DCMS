using VaultSharp;
using VaultSharp.V1.AuthMethods;
using VaultSharp.V1.AuthMethods.AppRole;
using VaultSharp.V1.AuthMethods.Token;

namespace Dcms.Shared.Vault;

/// <summary>How this process proved its identity to Vault. Shapes the diagnostics.</summary>
public enum VaultAuthMode
{
    /// <summary>A token handed to the process in its environment.</summary>
    Token,

    /// <summary>A role id + secret id the process exchanges for a short-lived token itself.</summary>
    AppRole,
}

/// <summary>
/// Resolves Vault credentials from the environment, preferring AppRole over a static token.
///
/// <para><b>Why AppRole matters here.</b> The static-token deployment gives every service the
/// same 72-hour periodic token, with a policy covering <c>secret/data/dcms/*</c>. Two
/// consequences, both of which have bitten this platform:</para>
///
/// <list type="bullet">
///   <item><b>It expires.</b> A periodic token still has to be renewed inside its period, and
///   nothing renews it. The configuration provider loads once at startup, so an expired token
///   is invisible until the next restart — at which point every service fails at once, usually
///   during a deploy, and looks like the deploy broke something.</item>
///   <item><b>There is no separation.</b> One token that can read <c>dcms/*</c> means
///   ai-gateway can read identity's secrets and site-builder can read the SMTP relay password.
///   The blast radius of any one container is every secret the platform holds.</item>
/// </list>
///
/// <para>With AppRole each service logs in itself, receives a short-lived token scoped by a
/// policy naming only <c>secret/data/dcms/shared</c> and <c>secret/data/dcms/{service}</c>, and
/// re-logs in on every start. Nothing to renew and nothing to rotate by hand.</para>
///
/// <para>The secret id is itself a secret, and a long-lived one in a file is the weak point of
/// this scheme. It is the right trade at two nodes; response-wrapped delivery through a Vault
/// Agent is the hardening step, and does not change any of this code.</para>
/// </summary>
public sealed record VaultCredentials(string Address, IAuthMethodInfo Auth, VaultAuthMode Mode)
{
    /// <summary>
    /// Reads VAULT_ADDR plus either VAULT_ROLE_ID/VAULT_SECRET_ID or VAULT_TOKEN.
    /// Returns null when Vault is not configured at all, which is a supported state — a bare
    /// `dotnet run` should not need a Vault.
    /// </summary>
    /// <param name="refuseDevToken">
    /// When set, a recognisably dev-mode root token is a startup failure rather than something
    /// to run with. Comes from DCMS_REFUSE_DEV_VAULT.
    /// </param>
    public static VaultCredentials? FromEnvironment(bool refuseDevToken)
    {
        var address = Environment.GetEnvironmentVariable("VAULT_ADDR");
        if (string.IsNullOrWhiteSpace(address))
        {
            return null;
        }

        var roleId = Environment.GetEnvironmentVariable("VAULT_ROLE_ID");
        var secretId = Environment.GetEnvironmentVariable("VAULT_SECRET_ID");

        // AppRole wins when both halves are present. This is what lets a deployment migrate one
        // service at a time: the base compose still sets a dev VAULT_TOKEN for everything, and a
        // service gains AppRole simply by being given the two variables.
        if (!string.IsNullOrWhiteSpace(roleId) && !string.IsNullOrWhiteSpace(secretId))
        {
            return new VaultCredentials(address, new AppRoleAuthMethodInfo(roleId, secretId), VaultAuthMode.AppRole);
        }

        var token = Environment.GetEnvironmentVariable("VAULT_TOKEN");
        if (string.IsNullOrWhiteSpace(token))
        {
            return null;
        }

        if (refuseDevToken && IsDevToken(token))
        {
            throw new InvalidOperationException(
                "Refusing to start: VAULT_TOKEN is a dev-mode root token but DCMS_REFUSE_DEV_VAULT is set. " +
                "Configure this environment with an AppRole (VAULT_ROLE_ID + VAULT_SECRET_ID), or supply a " +
                "token issued by a real Vault.");
        }

        return new VaultCredentials(address, new TokenAuthMethodInfo(token), VaultAuthMode.Token);
    }

    public IVaultClient CreateClient() => new VaultClient(new VaultClientSettings(Address, Auth));

    /// <summary>
    /// Recognises the tokens that mean "this is the throwaway dev Vault". Deliberately a
    /// denylist of shapes rather than an allowlist: the point is to catch a dev value that
    /// reached production, not to validate a real token.
    /// </summary>
    public static bool IsDevToken(string? token) =>
        !string.IsNullOrWhiteSpace(token) &&
        (token == "dcms-dev-root" || token.StartsWith("hvs.dev", StringComparison.OrdinalIgnoreCase) ||
         token.StartsWith("root", StringComparison.OrdinalIgnoreCase) || token == "dev-root");
}
