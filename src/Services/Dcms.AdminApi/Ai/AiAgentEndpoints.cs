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
/// Browser agent surface: (1) per-user credential linking — each user connects their own API key
/// (stored as Vault Transit ciphertext, never returned), and (2) a streaming proxy that forwards
/// the turn to ai-gateway with the caller's <b>authenticated</b> user id, so the key is resolved
/// and injected server-side and never reaches the browser. The agent loop and its tools run in
/// the browser against the live VFS; only model turns pass here.
///
/// <para>The request body is in the Anthropic Messages format because that is what the browser
/// loop speaks; which provider actually serves it is ai-gateway's decision, and it translates
/// where it has to. Nothing here is Anthropic-specific any more.</para>
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
            /*
             * Unset means INHERIT, not Anthropic.
             *
             * It used to default to Anthropic, which quietly pinned it on the user's row the
             * first time they saved anything — so a workspace configured for Ollama or OpenAI
             * still asked every one of its users for an Anthropic key, and nothing on the
             * settings page said why. A user who names no provider follows the workspace, which
             * is what "connect your own key" should have meant all along.
             */
            var provider = AiProvider.Inherit;
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

        /*
         * `/messages`, renamed from `/anthropic/messages` when the agent stopped being
         * Anthropic-only.
         *
         * <b>There is deliberately no alias on the old path.</b> One was added and taken out
         * again: two routes carrying the same audit action is exactly what
         * `Declared_actions_are_not_accidentally_shared` refuses, and it is right to — reading
         * `ai.request` in the log and not knowing which endpoint served it is the ambiguity that
         * guard exists to prevent. Exempting the alias instead would be worse: this record is
         * how "who used the tenant's API key, and when" gets answered.
         *
         * What the alias bought was small. A browser holding the previous bundle across a deploy
         * gets one failed turn with a visible error in the transcript — not silence, and not lost
         * work — and it is already in that state anyway, because the agent panel is a lazily
         * loaded chunk whose hashed filename changed in the same deploy. One reload fixes it.
         */
        app.MapPost("/api/admin/ai/messages", MessagesProxy)
            .RequirePermission(PlatformPermissions.SiteEdit)
            .WithAudit(AuditActions.AiRequestProxied, null, AuditCategory.Access);

        return app;
    }

    private static readonly Delegate MessagesProxy = async (
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

            // The one upstream header worth forwarding. A 429 without it tells the browser it
            // has been refused and nothing about when to come back, so the panel can only say
            // "later" — which is the difference between a limit somebody can work with and one
            // that just looks broken.
            if (upstream.Headers.RetryAfter?.Delta is { } delta)
            {
                ctx.Response.Headers.RetryAfter = ((int)delta.TotalSeconds).ToString();
            }
            ctx.Features.Get<Microsoft.AspNetCore.Http.Features.IHttpResponseBodyFeature>()?.DisableBuffering();

            await using var upstreamStream = await upstream.Content.ReadAsStreamAsync(ct);
            await upstreamStream.CopyToAsync(ctx.Response.Body, ct);
            upstream.Dispose();
            return Results.Empty;
    };

    private sealed record UpdateUserCredentialsRequest(
        string? Provider, string? Model, string? BaseUrl, string? ApiKey, bool ClearApiKey = false);
}
