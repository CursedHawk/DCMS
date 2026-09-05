using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using VaultSharp;
using VaultSharp.V1.SecretsEngines.Transit;

namespace Dcms.Shared.Vault;

/// <summary>
/// Envelope encryption via Vault Transit (mount "transit", key "dcms-tenant-secrets").
/// Tenant AI keys are stored as ciphertext and only decrypted in ai-gateway at
/// call time, so a database leak never exposes a usable key.
///
/// <para>Every call goes through <see cref="VaultTokenRefresh"/>, which is what keeps this
/// working past the first hour of a process's life. See that type for the whole story.</para>
/// </summary>
public sealed class VaultTransitEncryptor(IVaultClient vault, ILogger<VaultTransitEncryptor> logger)
    : ITransitEncryptor
{
    private const string Mount = "transit";

    public Task<string> EncryptAsync(string keyName, ReadOnlyMemory<byte> plaintext, CancellationToken ct = default)
    {
        // Encoded out here because a Span cannot be captured by the lambda below.
        var base64 = Convert.ToBase64String(plaintext.Span);

        return VaultTokenRefresh.ExecuteAsync(
            async () =>
            {
                var response = await vault.V1.Secrets.Transit.EncryptAsync(
                    keyName,
                    new EncryptRequestOptions { Base64EncodedPlainText = base64 },
                    mountPoint: Mount);
                return response.Data.CipherText;
            },
            vault.V1.Auth.ResetVaultToken,
            logger,
            $"encrypt with Transit key '{keyName}'");
    }

    public Task<byte[]> DecryptAsync(string keyName, string ciphertext, CancellationToken ct = default)
    {
        return VaultTokenRefresh.ExecuteAsync(
            async () =>
            {
                var response = await vault.V1.Secrets.Transit.DecryptAsync(
                    keyName,
                    new DecryptRequestOptions { CipherText = ciphertext },
                    mountPoint: Mount);
                return Convert.FromBase64String(response.Data.Base64EncodedPlainText);
            },
            vault.V1.Auth.ResetVaultToken,
            logger,
            $"decrypt with Transit key '{keyName}'");
    }
}

public static class VaultTransitServiceCollectionExtensions
{
    public const string TenantSecretsKey = "dcms-tenant-secrets";

    /// <summary>
    /// Tenant Meta (Facebook/Instagram) OAuth tokens. A key of its own rather than
    /// <see cref="TenantSecretsKey"/> because admin-api needs to *decrypt* these — it is the
    /// service that calls the Graph API — and reusing the AI key would have extended that
    /// decrypt over every tenant's AI provider key too. See infra/vault/policies/dcms-admin-api.hcl.
    /// </summary>
    public const string SocialTokensKey = "dcms-social-tokens";

    /// <summary>
    /// TLS private keys for tenant domains, held by the edge. A key of its own for the same
    /// reason as the two above: the edge is the most exposed process on the platform, and a
    /// shared Transit key would let a compromise there decrypt every tenant's AI provider key
    /// and Meta token as well. Its Vault policy grants decrypt on this key and nothing else.
    /// </summary>
    public const string TlsKeysKey = "dcms-tls-keys";

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
    ///
    /// <para>The client is a singleton, and one that holds a token far shorter-lived than the
    /// process. <see cref="VaultTransitEncryptor"/> re-authenticates on rejection rather than
    /// this registration handing out a new client per call: a fresh client would log in on every
    /// single Transit call, which is a login per TLS handshake on the edge.</para>
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
