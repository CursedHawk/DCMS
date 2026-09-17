using Dcms.Shared.Audit;
using Dcms.Shared.Audit.Http;
using System.Text;
using Dcms.AdminApi.Tenancy;
using Dcms.Shared.Data.Ai;
using Dcms.Shared.Kernel.Abstractions;
using Dcms.Shared.Security;
using Dcms.Shared.Vault;
using Microsoft.EntityFrameworkCore;

namespace Dcms.AdminApi.Ai;

/// <summary>
/// Per-tenant AI configuration. The API key is accepted once on write, encrypted
/// via Vault Transit, and never returned — GET reports only whether a key is set.
/// </summary>
public static class AiSettingsEndpoints
{
    public static IEndpointRouteBuilder MapAiSettingsEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/admin/ai/settings", async (
            AiDbContext db, ITenantContext tenant, CancellationToken ct) =>
        {
            var tenantId = tenant.TenantId!.Value;
            var settings = await db.Settings.AsNoTracking().FirstOrDefaultAsync(s => s.TenantId == tenantId, ct);
            return Results.Ok(new
            {
                provider = (settings?.Provider ?? AiProvider.Inherit).ToString(),
                model = settings?.Model,
                baseUrl = settings?.BaseUrl,
                hasApiKey = !string.IsNullOrEmpty(settings?.ApiKeyCiphertext),
            });
        }).RequirePermission(PlatformPermissions.AiSettings);

        app.MapPut("/api/admin/ai/settings", async (
            UpdateAiSettingsRequest body, AiDbContext db, ITenantContext tenant, CurrentUser me,
            ITransitEncryptor encryptor, IConfiguration config, CancellationToken ct) =>
        {
            if (!Enum.TryParse<AiProvider>(body.Provider, ignoreCase: true, out var provider))
            {
                return Results.BadRequest(new { error = "Unknown provider." });
            }

            // Same constraint as the per-user credential endpoint: this URL becomes an
            // outbound destination that ai-gateway attaches an API key to. A "local" provider
            // may only point at a private host the operator has allow-listed (SEC-01).
            if (AiBaseUrl.Validate(body.BaseUrl, provider, AllowedLocalHosts(config)) is { } baseUrlError)
            {
                return Results.BadRequest(new { error = baseUrlError });
            }

            var tenantId = tenant.TenantId!.Value;
            var settings = await db.Settings.FirstOrDefaultAsync(s => s.TenantId == tenantId, ct);
            if (settings is null)
            {
                settings = new TenantAiSettings { TenantId = tenantId };
                db.Settings.Add(settings);
            }

            settings.Provider = provider;
            settings.Model = body.Model;
            settings.BaseUrl = body.BaseUrl;
            settings.UpdatedBy = me.UserId;
            settings.UpdatedAt = DateTimeOffset.UtcNow;

            if (!string.IsNullOrEmpty(body.ApiKey))
            {
                settings.ApiKeyCiphertext = await encryptor.EncryptAsync(
                    VaultTransitServiceCollectionExtensions.TenantSecretsKey,
                    Encoding.UTF8.GetBytes(body.ApiKey), ct);
            }
            else if (body.ClearApiKey)
            {
                settings.ApiKeyCiphertext = null;
            }

            await db.SaveChangesAsync(ct);
            return Results.NoContent();
        }).RequirePermission(PlatformPermissions.AiSettings).WithAudit(AuditActions.AiSettingsUpdated, "ai_settings");

        return app;
    }

    /// <summary>
    /// Hosts an operator has declared safe for a local AI provider (Ollama / LM Studio), read
    /// from <c>Ai:AllowedLocalHosts</c>. Empty by default, which is what makes a tenant-supplied
    /// "local" base URL unable to reach an internal service. (SEC-01)
    /// </summary>
    internal static IReadOnlySet<string> AllowedLocalHosts(IConfiguration config) =>
        (config.GetSection("Ai:AllowedLocalHosts").Get<string[]>() ?? [])
            .Where(h => !string.IsNullOrWhiteSpace(h))
            .Select(h => h.Trim())
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

    private sealed record UpdateAiSettingsRequest(
        string Provider, string? Model, string? BaseUrl, string? ApiKey, bool ClearApiKey = false);
}
