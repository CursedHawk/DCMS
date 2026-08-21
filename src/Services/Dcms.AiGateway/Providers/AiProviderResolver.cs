using Dcms.Shared.Audit;
using System.Text;
using Dcms.Shared.Data.Ai;
using Dcms.Shared.Vault;
using Microsoft.EntityFrameworkCore;

namespace Dcms.AiGateway.Providers;

public sealed record ResolvedProvider(IChatProvider Provider, string Model);

/// <summary>Raw resolved credentials, for callers that proxy the provider API directly (e.g. the IDE agent messages endpoint) rather than using an <see cref="IChatProvider"/>.</summary>
public sealed record ResolvedCredentials(AiProvider Provider, string Model, string? BaseUrl, string ApiKey);

/// <summary>
/// Resolves the effective AI provider for a (tenant, user): the user's own
/// settings take precedence, then the tenant's, then the platform global defaults
/// from Vault KV (Ai:Defaults:*). Each field (provider, model, base URL, API key)
/// is resolved independently down that chain, so a user row overrides the tenant
/// row only where it sets a value. API keys are Vault Transit ciphertext, decrypted
/// here at call time and never logged.
/// </summary>
public sealed class AiProviderResolver(
    AiDbContext db, ITransitEncryptor encryptor, IConfiguration config, IAuditRecorder audit)
{
    public async Task<ResolvedProvider> ResolveAsync(Guid tenantId, Guid? userId = null, CancellationToken ct = default)
    {
        var c = await ResolveCredentialsAsync(tenantId, userId, ct);
        IChatProvider impl = c.Provider switch
        {
            AiProvider.Anthropic => new AnthropicProvider(c.ApiKey, c.BaseUrl),
            AiProvider.OpenAi => new OpenAiCompatibleProvider("openai", c.ApiKey, c.BaseUrl),
            AiProvider.Ollama => new OpenAiCompatibleProvider("ollama", c.ApiKey, c.BaseUrl ?? "http://localhost:11434/v1"),
            AiProvider.LmStudio => new OpenAiCompatibleProvider("lmstudio", c.ApiKey, c.BaseUrl ?? "http://localhost:1234/v1"),
            _ => new AnthropicProvider(c.ApiKey, c.BaseUrl),
        };
        return new ResolvedProvider(impl, c.Model);
    }

    public async Task<ResolvedCredentials> ResolveCredentialsAsync(Guid tenantId, Guid? userId, CancellationToken ct)
    {
        var userSettings = userId is { } uid
            ? await db.UserSettings.AsNoTracking().FirstOrDefaultAsync(s => s.TenantId == tenantId && s.UserId == uid, ct)
            : null;
        var tenantSettings = await db.Settings.AsNoTracking().FirstOrDefaultAsync(s => s.TenantId == tenantId, ct);

        var provider =
            userSettings is { Provider: not AiProvider.Inherit } ? userSettings.Provider :
            tenantSettings is { Provider: not AiProvider.Inherit } ? tenantSettings.Provider :
            ParseProvider(config["Ai:Defaults:Provider"]);

        var model = userSettings?.Model
                    ?? tenantSettings?.Model
                    ?? config["Ai:Defaults:Model"]
                    ?? DefaultModel(provider);

        var baseUrl = userSettings?.BaseUrl ?? tenantSettings?.BaseUrl ?? config["Ai:Defaults:BaseUrl"];

        var apiKey = await ResolveKeyAsync(
            userSettings?.ApiKeyCiphertext, tenantSettings?.ApiKeyCiphertext, tenantId, userId, ct);

        return new ResolvedCredentials(provider, model, baseUrl, apiKey);
    }

    private async Task<string> ResolveKeyAsync(
        string? userCiphertext, string? tenantCiphertext, Guid tenantId, Guid? userId, CancellationToken ct)
    {
        var owner = !string.IsNullOrEmpty(userCiphertext) ? "user"
            : !string.IsNullOrEmpty(tenantCiphertext) ? "tenant"
            : null;
        var ciphertext = owner == "user" ? userCiphertext : owner == "tenant" ? tenantCiphertext : null;

        if (ciphertext is not null)
        {
            // A stored provider credential is being decrypted and handed to an outbound call.
            // Nothing in the database changes, so no other layer sees this happen — and "who
            // used the tenant's API key, and when" is precisely the sort of question a bill
            // nobody recognises turns into.
            audit.Record(AuditActions.SecretAccessed)
                .InTenant(tenantId)
                .For("ai_credentials", owner)
                .As(AuditCategory.Access)
                .With("key_owner", owner)
                .With("user_id", userId);

            var bytes = await encryptor.DecryptAsync(
                VaultTransitServiceCollectionExtensions.TenantSecretsKey, ciphertext, ct);
            return Encoding.UTF8.GetString(bytes);
        }

        // The platform's own key, not the tenant's. Worth telling apart: the tenant is not
        // paying for this call and did not supply the credential.
        return config["Ai:Defaults:ApiKey"] ?? string.Empty;
    }

    private static AiProvider ParseProvider(string? value)
        => Enum.TryParse<AiProvider>(value, ignoreCase: true, out var p) && p != AiProvider.Inherit
            ? p
            : AiProvider.Anthropic;

    private static string DefaultModel(AiProvider provider) => provider switch
    {
        AiProvider.Anthropic => "claude-opus-4-8",
        AiProvider.OpenAi => "gpt-4o",
        AiProvider.Ollama => "llama3",
        AiProvider.LmStudio => "local-model",
        _ => "claude-opus-4-8",
    };
}
