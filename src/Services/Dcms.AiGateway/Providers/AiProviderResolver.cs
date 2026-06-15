using System.Text;
using Dcms.Shared.Data.Ai;
using Dcms.Shared.Vault;
using Microsoft.EntityFrameworkCore;

namespace Dcms.AiGateway.Providers;

public sealed record ResolvedProvider(IChatProvider Provider, string Model);

/// <summary>
/// Resolves the effective AI provider for a tenant: the tenant's own settings if
/// configured, otherwise the platform global defaults from Vault KV
/// (Ai:Defaults:*). Tenant API keys are Vault Transit ciphertext, decrypted here
/// at call time and never logged.
/// </summary>
public sealed class AiProviderResolver(AiDbContext db, ITransitEncryptor encryptor, IConfiguration config)
{
    public async Task<ResolvedProvider> ResolveAsync(Guid tenantId, CancellationToken ct)
    {
        var settings = await db.Settings.AsNoTracking().FirstOrDefaultAsync(s => s.TenantId == tenantId, ct);

        var provider = settings is { Provider: not AiProvider.Inherit }
            ? settings.Provider
            : ParseProvider(config["Ai:Defaults:Provider"]);

        var model = settings?.Model
                    ?? config["Ai:Defaults:Model"]
                    ?? DefaultModel(provider);

        var baseUrl = settings?.BaseUrl ?? config["Ai:Defaults:BaseUrl"];

        var apiKey = await ResolveKeyAsync(settings, ct);

        IChatProvider impl = provider switch
        {
            AiProvider.Anthropic => new AnthropicProvider(apiKey, baseUrl),
            AiProvider.OpenAi => new OpenAiCompatibleProvider("openai", apiKey, baseUrl),
            AiProvider.Ollama => new OpenAiCompatibleProvider("ollama", apiKey, baseUrl ?? "http://localhost:11434/v1"),
            AiProvider.LmStudio => new OpenAiCompatibleProvider("lmstudio", apiKey, baseUrl ?? "http://localhost:1234/v1"),
            _ => new AnthropicProvider(apiKey, baseUrl),
        };
        return new ResolvedProvider(impl, model);
    }

    private async Task<string> ResolveKeyAsync(TenantAiSettings? settings, CancellationToken ct)
    {
        if (!string.IsNullOrEmpty(settings?.ApiKeyCiphertext))
        {
            var bytes = await encryptor.DecryptAsync(
                VaultTransitServiceCollectionExtensions.TenantSecretsKey, settings.ApiKeyCiphertext, ct);
            return Encoding.UTF8.GetString(bytes);
        }
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
