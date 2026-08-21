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
            ITransitEncryptor encryptor, CancellationToken ct) =>
        {
            if (!Enum.TryParse<AiProvider>(body.Provider, ignoreCase: true, out var provider))
            {
                return Results.BadRequest(new { error = "Unknown provider." });
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

    private sealed record UpdateAiSettingsRequest(
        string Provider, string? Model, string? BaseUrl, string? ApiKey, bool ClearApiKey = false);
}
