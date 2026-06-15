using Microsoft.Extensions.Configuration;

namespace Dcms.Shared.Vault;

public static class VaultConfigurationExtensions
{
    /// <summary>
    /// Adds Vault as a configuration source when VAULT_ADDR is set. Dev compose
    /// sets VAULT_ADDR + VAULT_TOKEN (dev root token); prod uses AppRole-issued
    /// tokens injected by the platform. Silently skipped when unset so the
    /// services still boot on a bare machine.
    /// </summary>
    public static IConfigurationBuilder AddDcmsVault(this IConfigurationBuilder builder, string serviceName)
    {
        var address = Environment.GetEnvironmentVariable("VAULT_ADDR");
        var token = Environment.GetEnvironmentVariable("VAULT_TOKEN");

        // Prod safety: the prod compose profile sets DCMS_REFUSE_DEV_VAULT=true so
        // a misconfigured deployment that still points at the dev-mode Vault root
        // token fails fast instead of running with an insecure secrets backend.
        var refuseDev = string.Equals(
            Environment.GetEnvironmentVariable("DCMS_REFUSE_DEV_VAULT"), "true", StringComparison.OrdinalIgnoreCase);
        if (refuseDev && IsDevToken(token))
        {
            throw new InvalidOperationException(
                "Refusing to start: VAULT_TOKEN is a dev-mode root token but DCMS_REFUSE_DEV_VAULT is set. " +
                "Configure a real Vault with an AppRole-issued token for this environment.");
        }

        if (!string.IsNullOrWhiteSpace(address) && !string.IsNullOrWhiteSpace(token))
        {
            builder.Add(new VaultConfigurationSource(serviceName, address, token));
        }

        return builder;
    }

    private static bool IsDevToken(string? token) =>
        !string.IsNullOrWhiteSpace(token) &&
        (token == "dcms-dev-root" || token.StartsWith("hvs.dev", StringComparison.OrdinalIgnoreCase) ||
         token.StartsWith("root", StringComparison.OrdinalIgnoreCase) || token == "dev-root");
}
