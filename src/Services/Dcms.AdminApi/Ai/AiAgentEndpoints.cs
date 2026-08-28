using Dcms.Shared.Audit;
using Dcms.Shared.Audit.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json.Nodes;
using Dcms.AdminApi.Tenancy;
using Dcms.Shared.Data.Ai;
using Dcms.Shared.Kernel.Abstractions;
using Dcms.Shared.Security;
using Dcms.Shared.Vault;
using Microsoft.EntityFrameworkCore;

namespace Dcms.AdminApi.Ai;

/// <summary>
/// Web-IDE agent surface: (1) per-user Anthropic credential linking — each user
/// connects their own API key (stored as Vault Transit ciphertext, never returned),
/// and (2) a streaming proxy that forwards a raw Anthropic Messages request to
/// ai-gateway with the caller's <b>authenticated</b> user id, so the key is resolved
/// and injected server-side and never reaches the browser. The agent loop and its
/// file tools run in the browser against the live VFS; only model turns pass here.
/// </summary>
public static class AiAgentEndpoints
{
    private const string AiScope = "dcms.ai";

    public static IEndpointRouteBuilder MapAiAgentEndpoints(this IEndpointRouteBuilder app)
    {
        // --- Per-user credential linking -------------------------------------

        app.MapGet("/api/admin/ai/user-credentials", async (
            AiDbContext db, ITenantContext tenant, CurrentUser me, CancellationToken ct) =>
        {
            var tenantId = tenant.TenantId!.Value;
            var userId = me.RequireUserId();
            var s = await db.UserSettings.AsNoTracking()
                .FirstOrDefaultAsync(x => x.TenantId == tenantId && x.UserId == userId, ct);
            return Results.Ok(new
            {
                provider = (s?.Provider ?? AiProvider.Inherit).ToString(),
                model = s?.Model,
                baseUrl = s?.BaseUrl,
                hasApiKey = !string.IsNullOrEmpty(s?.ApiKeyCiphertext),
            });
        }).RequirePermission(PlatformPermissions.SiteEdit);

        app.MapPut("/api/admin/ai/user-credentials", async (
            UpdateUserCredentialsRequest body, AiDbContext db, ITenantContext tenant, CurrentUser me,
            ITransitEncryptor encryptor, CancellationToken ct) =>
        {
            // Provider defaults to Anthropic — the agent is Claude-specific.
            var provider = AiProvider.Anthropic;
            if (!string.IsNullOrWhiteSpace(body.Provider)
                && !Enum.TryParse(body.Provider, ignoreCase: true, out provider))
            {
                return Results.BadRequest(new { error = "Unknown provider." });
            }

            // A base URL is an outbound destination this user chose, and ai-gateway attaches
            // an API key to everything it sends there. Constrain it before it is stored;
            // AiProviderResolver separately refuses to pair it with a key from a broader scope.
            if (AiBaseUrl.Validate(body.BaseUrl, provider) is { } baseUrlError)
            {
                return Results.BadRequest(new { error = baseUrlError });
            }

            var tenantId = tenant.TenantId!.Value;
            var userId = me.RequireUserId();
            var s = await db.UserSettings.FirstOrDefaultAsync(x => x.TenantId == tenantId && x.UserId == userId, ct);
            if (s is null)
            {
                s = new UserAiSettings { TenantId = tenantId, UserId = userId };
                db.UserSettings.Add(s);
            }

            s.Provider = provider;
            s.Model = body.Model;
            s.BaseUrl = body.BaseUrl;
            s.UpdatedAt = DateTimeOffset.UtcNow;

            if (!string.IsNullOrEmpty(body.ApiKey))
            {
                s.ApiKeyCiphertext = await encryptor.EncryptAsync(
                    VaultTransitServiceCollectionExtensions.TenantSecretsKey,
                    Encoding.UTF8.GetBytes(body.ApiKey), ct);
            }
            else if (body.ClearApiKey)
            {
                s.ApiKeyCiphertext = null;
            }

            await db.SaveChangesAsync(ct);
            return Results.NoContent();
        }).RequirePermission(PlatformPermissions.SiteEdit).WithAudit(AuditActions.AiCredentialsUpdated, "ai_credentials");

        app.MapDelete("/api/admin/ai/user-credentials", async (
            AiDbContext db, ITenantContext tenant, CurrentUser me, CancellationToken ct) =>
        {
            var tenantId = tenant.TenantId!.Value;
            var userId = me.RequireUserId();
            var s = await db.UserSettings.FirstOrDefaultAsync(x => x.TenantId == tenantId && x.UserId == userId, ct);
            if (s is not null)
            {
                db.UserSettings.Remove(s);
                await db.SaveChangesAsync(ct);
            }
            return Results.NoContent();
        }).RequirePermission(PlatformPermissions.SiteEdit).WithAudit(AuditActions.AiCredentialsDeleted, "ai_credentials");

        // --- Streaming message proxy (browser agent loop -> ai-gateway) -------

        app.MapPost("/api/admin/ai/anthropic/messages", async (
            JsonObject request, HttpContext ctx, ITenantContext tenant, CurrentUser me,
            IServiceTokenProvider tokens, IHttpClientFactory httpClientFactory, CancellationToken ct) =>
        {
            if (request is null)
            {
                return Results.BadRequest(new { error = "An Anthropic Messages request body is required." });
            }

            var envelope = new
            {
                tenantId = tenant.TenantId!.Value,
                userId = me.RequireUserId(), // authoritative — never trust a client-supplied id
                request,
            };

            var token = await tokens.GetTokenAsync(AiScope, ct);
            var client = httpClientFactory.CreateClient("ai-gateway");
            using var upstreamRequest = new HttpRequestMessage(HttpMethod.Post, "/v1/messages")
            {
                Content = JsonContent.Create(envelope),
            };
            upstreamRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

            var upstream = await client.SendAsync(upstreamRequest, HttpCompletionOption.ResponseHeadersRead, ct);

            ctx.Response.StatusCode = (int)upstream.StatusCode;
            ctx.Response.ContentType = upstream.Content.Headers.ContentType?.ToString() ?? "application/json";
            ctx.Features.Get<Microsoft.AspNetCore.Http.Features.IHttpResponseBodyFeature>()?.DisableBuffering();

            await using var upstreamStream = await upstream.Content.ReadAsStreamAsync(ct);
            await upstreamStream.CopyToAsync(ctx.Response.Body, ct);
            upstream.Dispose();
            return Results.Empty;
        }).RequirePermission(PlatformPermissions.SiteEdit).WithAudit(AuditActions.AiRequestProxied, null, AuditCategory.Access);

        return app;
    }

    private sealed record UpdateUserCredentialsRequest(
        string? Provider, string? Model, string? BaseUrl, string? ApiKey, bool ClearApiKey = false);
}
