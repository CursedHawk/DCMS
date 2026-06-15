using Microsoft.Extensions.Configuration;
using VaultSharp;
using VaultSharp.V1.AuthMethods.Token;

namespace Dcms.Shared.Vault;

/// <summary>
/// Loads KV v2 secrets from secret/dcms/shared and secret/dcms/{service} into
/// configuration. Secret keys use "__" as the section separator, mirroring
/// environment-variable conventions (e.g. ConnectionStrings__Postgres).
/// </summary>
public sealed class VaultConfigurationSource(string serviceName, string address, string token) : IConfigurationSource
{
    public IConfigurationProvider Build(IConfigurationBuilder builder)
        => new VaultConfigurationProvider(serviceName, address, token);
}

public sealed class VaultConfigurationProvider(string serviceName, string address, string token)
    : ConfigurationProvider
{
    public override void Load()
    {
        var client = new VaultClient(new VaultClientSettings(address, new TokenAuthMethodInfo(token)));
        var data = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);

        foreach (var path in new[] { "dcms/shared", $"dcms/{serviceName}" })
        {
            try
            {
                var secret = client.V1.Secrets.KeyValue.V2
                    .ReadSecretAsync(path, mountPoint: "secret")
                    .GetAwaiter()
                    .GetResult();

                foreach (var kvp in secret.Data.Data)
                {
                    var key = kvp.Key.Replace("__", ConfigurationPath.KeyDelimiter, StringComparison.Ordinal);
                    data[key] = kvp.Value?.ToString();
                }
            }
            catch (VaultSharp.Core.VaultApiException)
            {
                // Path absent is fine — a service may have no dedicated secrets yet.
            }
        }

        Data = data;
    }
}
