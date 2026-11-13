using System.Text.Json;
using Dcms.AdminApi.Tenancy;
using Dcms.Shared.Audit;
using Dcms.Shared.Audit.Http;
using Dcms.Shared.Data;
using Dcms.Shared.Data.Ai;
using Dcms.Shared.Data.Rls;
using Dcms.Shared.Data.Tenancy;
using Dcms.Shared.Kernel.Abstractions;
using Microsoft.EntityFrameworkCore;

namespace Dcms.AdminApi.Ai;

/// <summary>
/// The tenant's AI agent policy: how many turns a site agent may take in one run, and the
/// overrides of the agents' system prompts.
///
/// <para>A platform decision, like the storage limit: the platform console sets it (by
/// delegation through platform-api), and no tenant permission reaches the write. The tenant's
/// agents read it — the loop is in the browser (D1), so this is where it learns the limit and the
/// prompts it is to use. The shipped prompts themselves live in <c>packages/core/src/aiPrompts.ts</c>;
/// only overrides are stored.</para>
/// </summary>
public static class AiAgentPolicyEndpoints
{
    /// <summary>The browser catalogue's keys (<c>packages/core/src/aiPrompts.ts</c>); a test keeps them in step.</summary>
    public static readonly IReadOnlySet<string> BrowserPromptKeys = new HashSet<string>(StringComparer.Ordinal)
    {
        "site.react", "site.static", "site.visual", "site.functions", "site.rules",
        "assistant", "assistant.writes", "assistant.attachments", "assistant.dataPlatform",
        "compaction", "knowledge",
    };

    /// <summary>Every prompt a tenant may override: the browser agents' and the server-built ones.</summary>
    public static readonly IReadOnlySet<string> PromptKeys =
        BrowserPromptKeys.Concat(ServerPrompts.Keys).ToHashSet(StringComparer.Ordinal);

    private const int MaxPromptChars = 100_000;

    public static IEndpointRouteBuilder MapAiAgentPolicyEndpoints(this IEndpointRouteBuilder app)
    {
        // The tenant's own agents. Any member may read it: it says how their agent behaves, and
        // holds nothing secret.
        app.MapGet("/api/admin/ai/agent-policy", async (AiDbContext db, ITenantContext tenant, CancellationToken ct) =>
        {
            if (!tenant.HasTenant) return Results.BadRequest(new { error = "No tenant." });
            var tenantId = tenant.TenantId!.Value;
            // rls: the request's own tenant, which the interceptor has told the database.
            var settings = await db.Settings.AsNoTracking().FirstOrDefaultAsync(s => s.TenantId == tenantId, ct);
            return Results.Ok(new
            {
                maxTurns = settings?.AgentMaxTurns ?? TenantAiSettings.DefaultAgentMaxTurns,
                assistantMaxTurns = settings?.AssistantMaxTurns ?? TenantAiSettings.DefaultAssistantMaxTurns,
                compactAtTokens = settings?.CompactAtTokens ?? TenantAiSettings.DefaultCompactAtTokens,
                prompts = Prompts(settings),
            });
        }).RequireAuthorization();

        app.MapGet("/api/admin/tenants/{tenantId:guid}/ai-agent-policy", async (
            Guid tenantId, ConsoleCaller console, AiDbContext db, CancellationToken ct) =>
        {
            if (!console.Allowed) return Results.Forbid();
            using var rls = RlsScope.Tenant(tenantId);
            var settings = await db.Settings.AsNoTracking().FirstOrDefaultAsync(s => s.TenantId == tenantId, ct);
            return Results.Ok(new
            {
                maxTurns = settings?.AgentMaxTurns,
                defaultMaxTurns = TenantAiSettings.DefaultAgentMaxTurns,
                maxMaxTurns = TenantAiSettings.MaxAgentMaxTurns,
                assistantMaxTurns = settings?.AssistantMaxTurns,
                defaultAssistantMaxTurns = TenantAiSettings.DefaultAssistantMaxTurns,
                maxAssistantMaxTurns = TenantAiSettings.MaxAssistantMaxTurns,
                compactAtTokens = settings?.CompactAtTokens,
                defaultCompactAtTokens = TenantAiSettings.DefaultCompactAtTokens,
                minCompactAtTokens = TenantAiSettings.MinCompactAtTokens,
                maxCompactAtTokens = TenantAiSettings.MaxCompactAtTokens,
                prompts = Prompts(settings),
                // The server-built prompts, which the console cannot read from @dcms/core.
                serverPrompts = ServerPrompts.All.Select(p => new
                {
                    key = p.Key, title = p.Title, usedFor = p.UsedFor, placeholders = p.Placeholders, text = p.Text,
                }),
            });
        })
            .RequireAuthorization()
            .AllowConsoleService(
                "the platform console shows a tenant's AI agent policy; its API holds dcms.console and has "
                + "already checked the operator holds platform:tenants:read.");

        app.MapPut("/api/admin/tenants/{tenantId:guid}/ai-agent-policy", async (
            Guid tenantId, AgentPolicyRequest body, ConsoleCaller console, AiDbContext db, TenancyDbContext tenancy,
            IAuditRecorder audit, CancellationToken ct) =>
        {
            if (!console.Allowed) return Results.Forbid();
            if (body.MaxTurns is { } turns && turns is < 1 or > TenantAiSettings.MaxAgentMaxTurns)
            {
                return Results.BadRequest(new { error = $"The turn limit must be between 1 and {TenantAiSettings.MaxAgentMaxTurns}." });
            }
            if (body.AssistantMaxTurns is { } assistantTurns && assistantTurns is < 1 or > TenantAiSettings.MaxAssistantMaxTurns)
            {
                return Results.BadRequest(new { error = $"The assistant's turn limit must be between 1 and {TenantAiSettings.MaxAssistantMaxTurns}." });
            }
            if (body.CompactAtTokens is { } compactAt
                && compactAt is < TenantAiSettings.MinCompactAtTokens or > TenantAiSettings.MaxCompactAtTokens)
            {
                return Results.BadRequest(new
                {
                    error = $"Compaction must start between {TenantAiSettings.MinCompactAtTokens} and {TenantAiSettings.MaxCompactAtTokens} tokens.",
                });
            }

            var prompts = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var (key, text) in body.Prompts ?? [])
            {
                if (!PromptKeys.Contains(key)) return Results.BadRequest(new { error = $"Unknown prompt '{key}'." });
                if (string.IsNullOrWhiteSpace(text)) continue; // blank = back to the shipped prompt
                if (text.Length > MaxPromptChars)
                {
                    return Results.BadRequest(new { error = $"The '{key}' prompt is longer than {MaxPromptChars} characters." });
                }
                prompts[key] = text;
            }

            // No ambient tenant and no policy on tenancy.tenants -- see the storage limit.
            var tenant = await tenancy.Tenants.IgnoreTenantFilter().AsNoTracking()
                .FirstOrDefaultAsync(t => t.Id == tenantId.ToString(), ct);
            if (tenant is null) return Results.NotFound();

            using var rls = RlsScope.Tenant(tenantId);
            var settings = await db.Settings.FirstOrDefaultAsync(s => s.TenantId == tenantId, ct);
            if (settings is null)
            {
                settings = new TenantAiSettings { TenantId = tenantId };
                db.Settings.Add(settings);
            }

            audit.Declared?
                .Platform()
                .For("tenant", tenantId, tenant.Identifier)
                .With("maxTurnsFrom", settings.AgentMaxTurns)
                .With("maxTurnsTo", body.MaxTurns)
                .With("assistantMaxTurnsTo", body.AssistantMaxTurns)
                .With("compactAtTokensTo", body.CompactAtTokens)
                .With("prompts", string.Join(", ", prompts.Keys.Order(StringComparer.Ordinal)));
            settings.AgentMaxTurns = body.MaxTurns;
            settings.AssistantMaxTurns = body.AssistantMaxTurns;
            settings.CompactAtTokens = body.CompactAtTokens;
            settings.PromptOverridesJson = prompts.Count == 0 ? null : JsonSerializer.Serialize(prompts);
            settings.UpdatedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(ct);
            return Results.NoContent();
        })
            .RequireAuthorization()
            .WithAudit(AuditActions.PlatformTenantAiAgentPolicyChanged, "tenant")
            .AllowConsoleService(
                "the platform console sets a tenant's AI agent policy; its API holds dcms.console and has "
                + "already checked the operator holds platform:tenants:write.");

        return app;
    }

    private static Dictionary<string, string> Prompts(TenantAiSettings? settings) =>
        ServerPrompts.ParseOverrides(settings?.PromptOverridesJson);

    /// <summary>The whole policy: a null turn limit is the default, a missing prompt the shipped one.</summary>
    public sealed record AgentPolicyRequest(
        int? MaxTurns, Dictionary<string, string>? Prompts, int? AssistantMaxTurns = null, int? CompactAtTokens = null);
}
