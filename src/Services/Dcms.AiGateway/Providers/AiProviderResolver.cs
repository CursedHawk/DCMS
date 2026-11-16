using Dcms.Shared.Audit;
using System.Text;
using Dcms.Shared.Data.Ai;
using Dcms.Shared.Data.Rls;
using Dcms.Shared.Vault;
using Microsoft.EntityFrameworkCore;

namespace Dcms.AiGateway.Providers;

public sealed record ResolvedProvider(IChatProvider Provider, string Model, ResolvedCredentials Credentials);

/// <summary>Raw resolved credentials, for callers that proxy the provider API directly (e.g. the IDE agent messages endpoint) rather than using an <see cref="IChatProvider"/>.</summary>
public sealed record ResolvedCredentials(AiProvider Provider, string Model, string? BaseUrl, string ApiKey)
{
    /// <summary>Whose key pays, for the usage ledger: <see cref="AiUsageSources"/>.</summary>
    public string UsageSource { get; init; } = AiUsageSources.Platform;

    /// <summary>The workspace provider that served the call; null for the platform default and a member's own key.</summary>
    public Guid? ProviderId { get; init; }

    /// <summary>The member whose own key pays; set only when <see cref="UsageSource"/> is <c>user</c>.</summary>
    public Guid? PayingUserId { get; init; }

    /// <summary>A label for the usage page.</summary>
    public string ProviderName { get; init; } = "Platform default";
}

/// <summary>A picked provider that is not one of the workspace's (deleted since, or never was).</summary>
public sealed class AiProviderNotFoundException() : InvalidOperationException(
    "That AI provider is no longer configured for this workspace. Pick another, or start a new conversation.");

/// <summary>
/// Raised when the resolved base URL was chosen at a narrower scope than the resolved API
/// key belongs to — i.e. somebody would be sending a credential they do not own to a
/// destination they picked. A configuration problem the caller can fix, not a server fault,
/// so the endpoints turn it into a 400.
/// </summary>
public sealed class AiCredentialScopeException(string message) : InvalidOperationException(message);

/// <summary>
/// Resolves the effective AI provider for a (tenant, user, picked provider).
///
/// <para><b>Picked:</b> a workspace provider chosen when the agent was started is used whole —
/// the member's own settings do not apply to it — with the platform's values under it for what
/// it leaves unset.</para>
///
/// <para><b>Not picked:</b> the user's own settings take precedence, then the workspace's default
/// provider, then the platform global defaults from Vault KV (Ai:Defaults:*). Each field
/// (provider, model, base URL, API key) is resolved independently down that chain, so a user row
/// overrides the workspace only where it sets a value.</para>
///
/// <para>API keys are Vault Transit ciphertext, decrypted here at call time and never logged.</para>
/// </summary>
public sealed class AiProviderResolver(
    AiDbContext db, ITransitEncryptor encryptor, IConfiguration config, IAuditRecorder audit)
{
    public async Task<ResolvedProvider> ResolveAsync(Guid tenantId, Guid? userId = null, CancellationToken ct = default)
    {
        var c = await ResolveCredentialsAsync(tenantId, userId, providerId: null, ct);
        IChatProvider impl = c.Provider switch
        {
            AiProvider.Anthropic => new AnthropicProvider(c.ApiKey, c.BaseUrl),
            AiProvider.OpenAi => new OpenAiCompatibleProvider("openai", c.ApiKey, c.BaseUrl),
            AiProvider.Ollama => new OpenAiCompatibleProvider("ollama", c.ApiKey, c.BaseUrl ?? "http://localhost:11434/v1"),
            AiProvider.LmStudio => new OpenAiCompatibleProvider("lmstudio", c.ApiKey, c.BaseUrl ?? "http://localhost:1234/v1"),
            _ => new AnthropicProvider(c.ApiKey, c.BaseUrl),
        };
        return new ResolvedProvider(impl, c.Model, c);
    }

    public async Task<ResolvedCredentials> ResolveCredentialsAsync(
        Guid tenantId, Guid? userId, Guid? providerId, CancellationToken ct)
    {
        // ADR 0015. The tenant arrives in the calling service's request body; nothing ambient
        // carries it here. Without this the database shows no settings rows, and the lookup
        // "succeeds" by falling through to the platform's provider and key -- the tenant's own
        // BYOK key silently replaced by ours, which is the worst way for this to fail.
        using var rls = RlsScope.Tenant(tenantId);

        // A picked provider is used whole: the member chose it over their own settings.
        var picked = providerId is { } pid
            ? await db.Providers.AsNoTracking().FirstOrDefaultAsync(p => p.Id == pid && p.TenantId == tenantId, ct)
              ?? throw new AiProviderNotFoundException()
            : null;
        var userSettings = picked is null && userId is { } uid
            ? await db.UserSettings.AsNoTracking().FirstOrDefaultAsync(s => s.TenantId == tenantId && s.UserId == uid, ct)
            : null;
        var workspace = picked ?? await db.Providers.AsNoTracking()
            .Where(p => p.TenantId == tenantId)
            .OrderByDescending(p => p.IsDefault).ThenBy(p => p.CreatedAt)
            .FirstOrDefaultAsync(ct);

        var provider =
            userSettings is { Provider: not AiProvider.Inherit } ? userSettings.Provider :
            workspace is { Provider: not AiProvider.Inherit } ? workspace.Provider :
            ParseProvider(config["Ai:Defaults:Provider"]);

        var model = userSettings?.Model
                    ?? workspace?.Model
                    ?? config["Ai:Defaults:Model"]
                    ?? DefaultModel(provider);

        // Blank is "unset", not "an empty base URL". The row stores whatever the settings
        // form posted, and a cleared field arrives as "" rather than null.
        string? baseUrl;
        Scope baseUrlScope;
        if (!string.IsNullOrWhiteSpace(userSettings?.BaseUrl))
        {
            (baseUrl, baseUrlScope) = (userSettings.BaseUrl, Scope.User);
        }
        else if (!string.IsNullOrWhiteSpace(workspace?.BaseUrl))
        {
            (baseUrl, baseUrlScope) = (workspace.BaseUrl, Scope.Tenant);
        }
        else
        {
            (baseUrl, baseUrlScope) = (config["Ai:Defaults:BaseUrl"], Scope.Platform);
        }

        var keyScope = !string.IsNullOrEmpty(userSettings?.ApiKeyCiphertext) ? Scope.User
            : !string.IsNullOrEmpty(workspace?.ApiKeyCiphertext) ? Scope.Tenant
            : Scope.Platform;

        // A model on someone's own machine authenticates nothing: it is sent no key rather than
        // a broader one, and whoever chose its URL is who it is billed to (nobody, in money).
        if (keyScope < baseUrlScope && !RequiresApiKey(provider))
        {
            return Attributed(new ResolvedCredentials(provider, model, baseUrl, string.Empty), baseUrlScope, workspace, userId);
        }

        // The rule that makes a caller-supplied base URL safe: whoever chose the destination
        // must own the credential that gets sent there. Each field resolves independently down
        // the chain, so without this a user could set only a base URL — leaving their key slot
        // empty — and have the tenant's or the platform's key delivered to a host of their
        // choosing. A broader-scoped URL with a narrower-scoped key is fine (a tenant's proxy
        // carrying a user's own key); the reverse never is.
        if (keyScope < baseUrlScope)
        {
            audit.Record(AuditActions.SecretAccessDenied)
                .InTenant(tenantId)
                .For("ai_credentials", Name(keyScope))
                .As(AuditCategory.Security, AuditSeverity.Warning)
                .With("base_url_scope", Name(baseUrlScope))
                .With("key_scope", Name(keyScope))
                .With("user_id", userId)
                .Denied("a custom base URL may only be used with a key of the same or narrower scope");

            throw new AiCredentialScopeException(
                $"A {Name(baseUrlScope)}-level base URL cannot be used with a {Name(keyScope)}-level API key. " +
                "Configure an API key at the same level as the base URL, or clear the base URL.");
        }

        var apiKey = await ResolveKeyAsync(
            keyScope, userSettings?.ApiKeyCiphertext, workspace?.ApiKeyCiphertext, tenantId, userId, ct);

        return Attributed(new ResolvedCredentials(provider, model, baseUrl, apiKey), keyScope, workspace, userId);
    }

    /// <summary>Who pays for a call, for the usage ledger: the owner of the key it carries.</summary>
    private static ResolvedCredentials Attributed(
        ResolvedCredentials credentials, Scope payer, AiProviderConfig? workspace, Guid? userId) => payer switch
    {
        Scope.User => credentials with { UsageSource = AiUsageSources.User, PayingUserId = userId, ProviderName = "Own key" },
        Scope.Tenant => credentials with
        {
            UsageSource = AiUsageSources.Workspace, ProviderId = workspace?.Id, ProviderName = workspace?.Name ?? "Workspace",
        },
        // A workspace provider can carry only a model and leave the key to the platform: it is still
        // the provider that served the call, and the page charts it as one.
        _ => credentials with
        {
            UsageSource = AiUsageSources.Platform, ProviderId = workspace?.Id, ProviderName = workspace?.Name ?? "Platform default",
        },
    };

    /// <summary>Ollama and LM Studio run on the operator's own machine and take no key.</summary>
    public static bool RequiresApiKey(AiProvider provider) =>
        provider is not (AiProvider.Ollama or AiProvider.LmStudio);

    /// <summary>Which layer of the user → tenant → platform chain a value came from. Higher is more specific.</summary>
    private enum Scope { Platform = 0, Tenant = 1, User = 2 }

    private static string Name(Scope scope) => scope switch
    {
        Scope.User => "user",
        Scope.Tenant => "tenant",
        _ => "platform",
    };

    private async Task<string> ResolveKeyAsync(
        Scope scope, string? userCiphertext, string? tenantCiphertext, Guid tenantId, Guid? userId, CancellationToken ct)
    {
        var owner = scope == Scope.User ? "user" : scope == Scope.Tenant ? "tenant" : null;
        var ciphertext = scope == Scope.User ? userCiphertext : scope == Scope.Tenant ? tenantCiphertext : null;

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
