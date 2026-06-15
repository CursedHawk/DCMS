using Microsoft.Extensions.DependencyInjection;
using VaultSharp;
using VaultSharp.V1.AuthMethods.Token;
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
    /// Registers an IVaultClient (from VAULT_ADDR/VAULT_TOKEN) and the Transit
    /// encryptor. No-ops gracefully if Vault env vars are unset (encrypt/decrypt
    /// will then throw on use — acceptable for environments without AI configured).
    /// </summary>
    public static IServiceCollection AddDcmsVaultTransit(this IServiceCollection services)
    {
        services.AddSingleton<IVaultClient>(_ =>
        {
            var address = Environment.GetEnvironmentVariable("VAULT_ADDR") ?? "http://localhost:8200";
            var token = Environment.GetEnvironmentVariable("VAULT_TOKEN") ?? "dcms-dev-root";
            return new VaultClient(new VaultClientSettings(address, new TokenAuthMethodInfo(token)));
        });
        services.AddSingleton<ITransitEncryptor, VaultTransitEncryptor>();
        return services;
    }
}
