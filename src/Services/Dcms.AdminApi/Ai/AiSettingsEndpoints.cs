using Dcms.Shared.Audit;
using Dcms.Shared.Audit.Http;
using System.Text;
using Dcms.AdminApi.Tenancy;
using Dcms.Shared.Data.Ai;
using Dcms.Shared.Kernel.Abstractions;
using Dcms.Shared.Security;
using Dcms.Shared.Security.Authorization;
using Dcms.Shared.Vault;
using Microsoft.EntityFrameworkCore;

namespace Dcms.AdminApi.Ai;

/// <summary>
/// A workspace's AI providers, several per workspace with one default, and what it spent on
/// them. An API key is accepted on write, encrypted via Vault Transit, and never returned — reads
/// say only whether one is set.
/// </summary>
// rls: request tenant. Every query here is the request's own tenant (tenant.TenantId), which
// is what the interceptor tells the database.
public static class AiSettingsEndpoints
{
    public static IEndpointRouteBuilder MapAiSettingsEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/admin/ai/providers", async (AiDbContext db, ITenantContext tenant, CancellationToken ct) =>
        {
            var tenantId = tenant.TenantId!.Value;
            var rows = await db.Providers.AsNoTracking()
                .Where(p => p.TenantId == tenantId)
                .OrderByDescending(p => p.IsDefault).ThenBy(p => p.CreatedAt)
                .ToListAsync(ct);
            return Results.Ok(rows.Select(p => new
            {
                p.Id,
                p.Name,
                provider = p.Provider.ToString(),
                p.Model,
                p.BaseUrl,
                hasApiKey = !string.IsNullOrEmpty(p.ApiKeyCiphertext),
                p.IsDefault,
                p.UpdatedAt,
            }));
        }).RequirePermission(PlatformPermissions.AiSettings);

        app.MapPost("/api/admin/ai/providers", async (
            SaveProviderRequest body, AiDbContext db, ITenantContext tenant, CurrentUser me,
            ITransitEncryptor encryptor, IConfiguration config, CancellationToken ct) =>
        {
            var tenantId = tenant.TenantId!.Value;
            if (await db.Providers.CountAsync(p => p.TenantId == tenantId, ct) >= AiProviderConfig.MaxPerTenant)
            {
                return Results.BadRequest(new { error = $"A workspace can have at most {AiProviderConfig.MaxPerTenant} AI providers." });
            }
            var row = new AiProviderConfig { TenantId = tenantId };
            return await SaveAsync(row, body, isNew: true, db, me, encryptor, config, ct);
        }).RequirePermission(PlatformPermissions.AiSettings).WithAudit(AuditActions.AiProviderCreated, "ai_provider");

        app.MapPut("/api/admin/ai/providers/{id:guid}", async (
            Guid id, SaveProviderRequest body, AiDbContext db, ITenantContext tenant, CurrentUser me,
            ITransitEncryptor encryptor, IConfiguration config, CancellationToken ct) =>
        {
            var tenantId = tenant.TenantId!.Value;
            var row = await db.Providers.FirstOrDefaultAsync(p => p.Id == id && p.TenantId == tenantId, ct);
            return row is null
                ? Results.NotFound()
                : await SaveAsync(row, body, isNew: false, db, me, encryptor, config, ct);
        }).RequirePermission(PlatformPermissions.AiSettings).WithAudit(AuditActions.AiProviderUpdated, "ai_provider");

        app.MapDelete("/api/admin/ai/providers/{id:guid}", async (
            Guid id, AiDbContext db, ITenantContext tenant, IAuditRecorder audit, CancellationToken ct) =>
        {
            var tenantId = tenant.TenantId!.Value;
            var row = await db.Providers.FirstOrDefaultAsync(p => p.Id == id && p.TenantId == tenantId, ct);
            if (row is null) return Results.NotFound();

            await using var tx = await db.Database.BeginTransactionAsync(ct);
            db.Providers.Remove(row);
            await db.SaveChangesAsync(ct);
            // Conversations started on it go on with the default rather than failing every turn.
            // Set-based (a provider can have many), so the delete's record says how many moved.
            var moved = await db.Conversations
                .Where(c => c.TenantId == tenantId && c.ProviderId == id)
                .ExecuteUpdateAsync(u => u.SetProperty(c => c.ProviderId, (Guid?)null), ct);
            audit.Declared?.With("conversations_moved_to_default", moved);
            if (row.IsDefault)
            {
                var next = await db.Providers.Where(p => p.TenantId == tenantId).OrderBy(p => p.CreatedAt).FirstOrDefaultAsync(ct);
                if (next is not null)
                {
                    next.IsDefault = true;
                    await db.SaveChangesAsync(ct);
                }
            }
            await tx.CommitAsync(ct);
            return Results.NoContent();
        }).RequirePermission(PlatformPermissions.AiSettings).WithAudit(AuditActions.AiProviderDeleted, "ai_provider");

        // What a member may pick when starting an agent: names only, never a URL or a key.
        app.MapGet("/api/admin/ai/provider-choices", async (
            AiDbContext db, ITenantContext tenant, CurrentUser me, CancellationToken ct) =>
        {
            var tenantId = tenant.TenantId!.Value;
            var userId = me.RequireUserId();
            var providers = await db.Providers.AsNoTracking()
                .Where(p => p.TenantId == tenantId)
                .OrderByDescending(p => p.IsDefault).ThenBy(p => p.CreatedAt)
                .Select(p => new ProviderChoice(p.Id, p.Name, p.Provider.ToString(), p.Model, p.IsDefault))
                .ToListAsync(ct);
            var own = await OwnSettingsAsync(db, tenantId, userId, ct);
            return Results.Ok(new { providers, own });
        }).RequirePermission(PlatformPermissions.SiteEdit);

        // Daily usage: the workspace's (for those who manage its AI) and the caller's own key's.
        app.MapGet("/api/admin/ai/usage", async (
            int? days, AiDbContext db, ITenantContext tenant, CurrentUser me, IPermissionResolver permissions,
            CancellationToken ct) =>
        {
            var tenantId = tenant.TenantId!.Value;
            var userId = me.RequireUserId();
            var span = Math.Clamp(days ?? 30, 1, 366);
            var to = DateOnly.FromDateTime(DateTime.UtcNow);
            var from = to.AddDays(1 - span);

            var seesWorkspace = me.IsSuperAdmin
                || (await permissions.GetPermissionsAsync(tenantId, userId, ct)).Contains(PlatformPermissions.AiSettings);

            var rows = await db.Usage.AsNoTracking()
                .Where(u => u.TenantId == tenantId && u.Day >= from && u.Day <= to)
                .Where(u => u.Source == AiUsageSources.User ? u.UserId == userId : seesWorkspace)
                .OrderBy(u => u.Day)
                .Select(u => new UsageRow(
                    u.Day, u.Source, u.ProviderId, u.ProviderName, u.ProviderKind, u.Model,
                    u.Calls, u.InputTokens, u.OutputTokens, u.CacheReadTokens, u.CacheWriteTokens))
                .ToListAsync(ct);

            return Results.Ok(new
            {
                from,
                to,
                workspace = seesWorkspace ? rows.Where(r => r.Source != AiUsageSources.User) : null,
                mine = rows.Where(r => r.Source == AiUsageSources.User),
                own = await OwnSettingsAsync(db, tenantId, userId, ct),
            });
        }).RequirePermission(PlatformPermissions.SiteEdit);

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

    private static async Task<IResult> SaveAsync(
        AiProviderConfig row, SaveProviderRequest body, bool isNew, AiDbContext db, CurrentUser me,
        ITransitEncryptor encryptor, IConfiguration config, CancellationToken ct)
    {
        var name = body.Name?.Trim() ?? "";
        if (name.Length is 0 or > AiProviderConfig.MaxName)
        {
            return Results.BadRequest(new { error = $"A provider needs a name of at most {AiProviderConfig.MaxName} characters." });
        }
        if (!Enum.TryParse<AiProvider>(body.Provider, ignoreCase: true, out var provider))
        {
            return Results.BadRequest(new { error = "Unknown provider." });
        }
        var baseUrl = string.IsNullOrWhiteSpace(body.BaseUrl) ? null : body.BaseUrl.Trim();

        // This URL becomes an outbound destination that ai-gateway attaches an API key to. A
        // "local" provider may only point at a private host the operator has allow-listed (SEC-01).
        if (AiBaseUrl.Validate(baseUrl, provider, AllowedLocalHosts(config)) is { } baseUrlError)
        {
            return Results.BadRequest(new { error = baseUrlError });
        }
        if (await db.Providers.AnyAsync(p => p.TenantId == row.TenantId && p.Id != row.Id && p.Name == name, ct))
        {
            return Results.Conflict(new { error = $"There is already a provider called {name}." });
        }

        row.Name = name;
        row.Provider = provider;
        row.Model = string.IsNullOrWhiteSpace(body.Model) ? null : body.Model.Trim();
        row.BaseUrl = baseUrl;
        row.UpdatedBy = me.UserId;
        row.UpdatedAt = DateTimeOffset.UtcNow;
        if (!string.IsNullOrEmpty(body.ApiKey))
        {
            row.ApiKeyCiphertext = await encryptor.EncryptAsync(
                VaultTransitServiceCollectionExtensions.TenantSecretsKey, Encoding.UTF8.GetBytes(body.ApiKey), ct);
        }
        else if (body.ClearApiKey)
        {
            row.ApiKeyCiphertext = null;
        }

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        // The first provider is the default; making another the default unsets the old one first,
        // since at most one may be (a unique index).
        var first = isNew && !await db.Providers.AnyAsync(p => p.TenantId == row.TenantId, ct);
        if (first || (body.IsDefault == true && !row.IsDefault))
        {
            // Saved before the new default is: the index is not deferrable, so two defaults may
            // never exist even for the length of one statement.
            var previous = await db.Providers
                .Where(p => p.TenantId == row.TenantId && p.IsDefault && p.Id != row.Id)
                .ToListAsync(ct);
            foreach (var p in previous) p.IsDefault = false;
            if (previous.Count > 0) await db.SaveChangesAsync(ct);
            row.IsDefault = true;
        }
        if (isNew) db.Providers.Add(row);
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return isNew ? Results.Ok(new { id = row.Id }) : Results.NoContent();
    }

    /// <summary>The caller's own AI settings, when they override the workspace's; null when they do not.</summary>
    private static async Task<object?> OwnSettingsAsync(AiDbContext db, Guid tenantId, Guid userId, CancellationToken ct)
    {
        var s = await db.UserSettings.AsNoTracking().FirstOrDefaultAsync(x => x.TenantId == tenantId && x.UserId == userId, ct);
        if (s is null) return null;
        var hasKey = !string.IsNullOrEmpty(s.ApiKeyCiphertext);
        if (s.Provider == AiProvider.Inherit && !hasKey && string.IsNullOrWhiteSpace(s.Model) && string.IsNullOrWhiteSpace(s.BaseUrl))
        {
            return null;
        }
        return new { provider = s.Provider.ToString(), model = s.Model, hasApiKey = hasKey };
    }

    private sealed record SaveProviderRequest(
        string? Name, string? Provider, string? Model, string? BaseUrl, string? ApiKey,
        bool ClearApiKey = false, bool? IsDefault = null);

    private sealed record ProviderChoice(Guid Id, string Name, string Provider, string? Model, bool IsDefault);

    private sealed record UsageRow(
        DateOnly Day, string Source, Guid? ProviderId, string ProviderName, string ProviderKind, string Model,
        long Calls, long InputTokens, long OutputTokens, long CacheReadTokens, long CacheWriteTokens);
}
