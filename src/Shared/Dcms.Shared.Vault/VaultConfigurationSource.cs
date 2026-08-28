using Microsoft.Extensions.Configuration;

namespace Dcms.Shared.Vault;

/// <summary>
/// Loads KV v2 secrets from secret/dcms/shared and secret/dcms/{service} into
/// configuration. Secret keys use "__" as the section separator, mirroring
/// environment-variable conventions (e.g. ConnectionStrings__Postgres).
/// </summary>
public sealed class VaultConfigurationSource(string serviceName, VaultCredentials credentials, bool hardened)
    : IConfigurationSource
{
    public IConfigurationProvider Build(IConfigurationBuilder builder)
        => new VaultConfigurationProvider(serviceName, credentials, hardened);
}

/// <param name="hardened">
/// True in a deployment that has declared itself production-like (<c>DCMS_REFUSE_DEV_VAULT</c>).
/// Decides whether a Vault error that is not a missing path is fatal — see <see cref="Load"/>.
/// </param>
public sealed class VaultConfigurationProvider(string serviceName, VaultCredentials credentials, bool hardened)
    : ConfigurationProvider
{
    public override void Load()
    {
        // With AppRole this performs the login, so the token this client carries is minted here
        // and lives only as long as the process needs it. With a static token it is simply used.
        // Either way the client is built per Load(), so a restart is a fresh credential.
        var client = credentials.CreateClient();
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
            catch (VaultSharp.Core.VaultApiException ex) when (ex.StatusCode == 404)
            {
                // The one benign case: a service that has no dedicated secrets yet.
            }
            catch (VaultSharp.Core.VaultApiException) when (!hardened)
            {
                // Development: a half-configured Vault should not stop anyone working.
            }
            catch (VaultSharp.Core.VaultApiException ex)
            {
                // Everything else, in a production-like deployment, is fatal.
                //
                // This catch used to swallow every VaultApiException with the "path absent is
                // fine" reasoning above, which is true of a 404 and of nothing else. A 403 from
                // an expired token and a 503 from a sealed Vault were treated identically to a
                // service having no secrets: the provider returned an empty set and the service
                // started perfectly happily on compose env and in-code defaults. That is how an
                // expired token went unnoticed on vps1 until a deploy surfaced it — a running
                // container keeps its startup config, so nothing degrades until the next
                // restart, and then it degrades silently.
                //
                // Silence is the wrong default here because some settings have no source but
                // Vault: Visitor__SigningKey falls back to a key committed to this repository,
                // and Ai__Defaults__* simply vanish. Refusing to start matches what already
                // happens when Vault is unreachable (a connection failure was never caught) and
                // what the service-level guards in admin-api and content-api already do.
                throw new InvalidOperationException(Explain(path, ex), ex);
            }
        }

        Data = data;
    }

    /// <summary>
    /// Names the two failures that actually happen, because the raw VaultSharp message says
    /// "permission denied / invalid token" without saying which of the several possible causes
    /// it is — and this is read by someone with a service that will not boot.
    /// </summary>
    private string Explain(string path, VaultSharp.Core.VaultApiException ex) => ex.StatusCode switch
    {
        // A 403 means something different under each auth method, and saying which saves the
        // person reading this from chasing the wrong one. Under AppRole the token was minted
        // seconds ago, so expiry is not the explanation — the policy is.
        403 when credentials.Mode == VaultAuthMode.AppRole =>
               $"Refusing to start: Vault rejected this service's AppRole token reading secret/{path} (403). " +
               "The token was just minted, so this is a POLICY problem rather than an expiry: the role " +
               $"bound to '{serviceName}' has no read capability on that path. Check " +
               $"`vault read auth/approle/role/dcms-{serviceName}` and the policy it names.",
        403 => $"Refusing to start: Vault rejected the token reading secret/{path} (403). " +
               "The token has most likely expired or been revoked — a periodic token still has to be " +
               "renewed inside its period. Check `vault token lookup`, mint a replacement with the " +
               "dcms policy, and update VAULT_TOKEN. Moving this service to an AppRole " +
               "(VAULT_ROLE_ID + VAULT_SECRET_ID) removes the expiry entirely.",
        503 => $"Refusing to start: Vault is sealed or unavailable reading secret/{path} (503). " +
               "Unseal it (`vault operator unseal`) and restart this service.",
        _ => $"Refusing to start: Vault returned {ex.StatusCode} reading secret/{path}. " +
             $"{string.Join("; ", ex.ApiErrors ?? [])}",
    };
}
