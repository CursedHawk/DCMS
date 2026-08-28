using Microsoft.Extensions.Configuration;

namespace Dcms.Shared.Vault;

public static class VaultConfigurationExtensions
{
    /// <summary>
    /// Adds Vault as a configuration source when VAULT_ADDR is set, authenticating with an
    /// AppRole (VAULT_ROLE_ID + VAULT_SECRET_ID) when both are present and falling back to
    /// VAULT_TOKEN otherwise. Silently skipped when Vault is not configured at all, so the
    /// services still boot on a bare machine.
    /// </summary>
    public static IConfigurationBuilder AddDcmsVault(this IConfigurationBuilder builder, string serviceName)
    {
        // Prod safety: the prod compose profile sets DCMS_REFUSE_DEV_VAULT=true so a
        // misconfigured deployment that still points at the dev-mode Vault root token fails
        // fast instead of running with an insecure secrets backend.
        var refuseDev = string.Equals(
            Environment.GetEnvironmentVariable("DCMS_REFUSE_DEV_VAULT"), "true", StringComparison.OrdinalIgnoreCase);

        var credentials = VaultCredentials.FromEnvironment(refuseDev);
        if (credentials is null)
        {
            return builder;
        }

        // The same flag decides two things, and deliberately so: a deployment that says it
        // will not tolerate a dev Vault token is also one where a Vault that answers 403 or
        // 503 must stop the service rather than be quietly skipped.
        builder.Add(new VaultConfigurationSource(serviceName, credentials, refuseDev));
        return builder;
    }
}
