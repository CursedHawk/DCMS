using Microsoft.Extensions.DependencyInjection;
using VaultSharp;
using VaultSharp.V1.SecretsEngines.Transit;

namespace Dcms.Shared.Vault;

/// <summary>
/// Envelope encryption via Vault Transit (mount "transit", key "dcms-tenant-secrets").
/// Tenant AI keys are stored as ciphertext and only decrypted in ai-gateway at
/// call time, so a database leak never exposes a usable key.
/// </summary>
public sealed class VaultTransitEncryptor(IVaultClient vault) : ITransitEncryptor
{
    private const string Mount = "transit";

    public async Task<string> EncryptAsync(string keyName, ReadOnlyMemory<byte> plaintext, CancellationToken ct = default)
    {
        var response = await vault.V1.Secrets.Transit.EncryptAsync(
            keyName,
            new EncryptRequestOptions { Base64EncodedPlainText = Convert.ToBase64String(plaintext.Span) },
            mountPoint: Mount);
        return response.Data.CipherText;
    }

    public async Task<byte[]> DecryptAsync(string keyName, string ciphertext, CancellationToken ct = default)
    {
        var response = await vault.V1.Secrets.Transit.DecryptAsync(
            keyName,
            new DecryptRequestOptions { CipherText = ciphertext },
            mountPoint: Mount);
        return Convert.FromBase64String(response.Data.Base64EncodedPlainText);
    }
}

public static class VaultTransitServiceCollectionExtensions
{
    public const string TenantSecretsKey = "dcms-tenant-secrets";

    /// <summary>
    /// Registers an IVaultClient and the Transit encryptor, using the same credential
    /// resolution as the configuration provider — AppRole when available, token otherwise.
    ///
    /// <para>This used to build its own client with
    /// <c>VAULT_ADDR ?? "http://localhost:8200"</c> and <c>VAULT_TOKEN ?? "dcms-dev-root"</c>,
    /// with no <c>DCMS_REFUSE_DEV_VAULT</c> check — so the guard that stops the CONFIGURATION
    /// provider talking to a dev Vault did not apply to the client that decrypts tenants' AI
    /// provider keys. A production deployment that lost its VAULT_TOKEN would have failed
    /// startup on the config side and, had it not, silently tried a dev root token here.</para>
    ///
    /// <para>Still tolerant of Vault being absent: the client is registered lazily, so a
    /// deployment with no Vault starts fine and only throws if something actually asks for an
    /// encrypt or decrypt. That is the right shape — Transit is needed for tenant AI keys and
    /// nothing else on the startup path.</para>
    /// </summary>
    public static IServiceCollection AddDcmsVaultTransit(this IServiceCollection services)
    {
        services.AddSingleton<IVaultClient>(_ =>
        {
            var refuseDev = string.Equals(
                Environment.GetEnvironmentVariable("DCMS_REFUSE_DEV_VAULT"), "true", StringComparison.OrdinalIgnoreCase);

            var credentials = VaultCredentials.FromEnvironment(refuseDev)
                ?? throw new InvalidOperationException(
                    "Vault Transit was requested but Vault is not configured. Set VAULT_ADDR plus either "
                    + "VAULT_ROLE_ID/VAULT_SECRET_ID or VAULT_TOKEN. Transit decrypts tenant AI provider "
                    + "keys; without it those keys cannot be read.");

            return credentials.CreateClient();
        });
        services.AddSingleton<ITransitEncryptor, VaultTransitEncryptor>();
        return services;
    }
}
