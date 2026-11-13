using System.Diagnostics;
using Dcms.AiGateway.Providers;
using Dcms.Shared.Telemetry;
using Dcms.Shared.Data.Ai;
using Dcms.Shared.Data.Rls;
using Microsoft.EntityFrameworkCore;

namespace Dcms.AiGateway;

/// <summary>
/// Internal chat endpoint, called by admin-api with a dcms.ai client-credentials
/// token. Resolves the tenant's provider and returns the completion text.
/// </summary>
public static class ChatEndpoints
{
    public static IEndpointRouteBuilder MapChatEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/v1/chat", async (
            ChatCompletionRequest body, AiProviderResolver resolver, DcmsMetrics metrics,
            AiQuota quota, ILoggerFactory loggerFactory, CancellationToken ct) =>
        {
            if (body.TenantId == Guid.Empty || string.IsNullOrWhiteSpace(body.Prompt))
            {
                return Results.BadRequest(new { error = "tenantId and prompt are required." });
            }

            var logger = loggerFactory.CreateLogger("ai-gateway");

            // The same budget as the agent's. A tenant's AI spend is one number whether it was
            // the IDE, the dock, content generation or the site chatbot that spent it, and a
            // ceiling that only covered one of those would be a ceiling with a door in it.
            var verdict = await quota.CheckAsync(body.TenantId, body.UserId, ct);
            if (!verdict.Ok)
            {
                return Results.Json(
                    new { error = verdict.Error, message = verdict.Message },
                    statusCode: StatusCodes.Status429TooManyRequests);
            }

            ResolvedProvider resolved;
            try
            {
                resolved = await resolver.ResolveAsync(body.TenantId, body.UserId, ct);
            }
            catch (AiCredentialScopeException ex)
            {
                return Results.BadRequest(new { error = "credential_scope", message = ex.Message });
            }

            logger.LogInformation("Chat completion via {Provider}/{Model} for tenant {TenantId}",
                resolved.Provider.ProviderId, resolved.Model, body.TenantId);

            // The span wraps the provider call and nothing else, so the AI dashboard's latency
            // is the model's, not this handler's — the resolver above may have gone to Vault to
            // decrypt a key, which is our cost, not the vendor's.
            using var activity = DcmsActivitySource.Start("ai.chat.complete", ActivityKind.Client);
            activity?.SetTag("gen_ai.system", resolved.Provider.ProviderId);
            activity?.SetTag("gen_ai.request.model", resolved.Model);

            var started = Stopwatch.GetTimestamp();
            try
            {
                var completion = await resolved.Provider.CompleteAsync(
                    new ChatRequest(resolved.Model, body.System, body.Prompt, body.MaxTokens ?? 8192), ct);

                activity?.SetTag("gen_ai.usage.input_tokens", completion.PromptTokens);
                activity?.SetTag("gen_ai.usage.output_tokens", completion.CompletionTokens);

                // Model as a label is bounded by what the resolver will hand back, and it is the
                // dimension the bill is actually itemised by — an opus call and a haiku call at
                // the same token count are not the same money.
                metrics.AiCall(body.TenantId, resolved.Provider.ProviderId, resolved.Model,
                    completion.PromptTokens, completion.CompletionTokens, Stopwatch.GetElapsedTime(started));
                await quota.RecordAsync(
                    body.TenantId, completion.PromptTokens + completion.CompletionTokens, ct);

                return Results.Ok(new ChatCompletionResponse(resolved.Provider.ProviderId, resolved.Model, completion.Text));
            }
            catch (Exception ex)
            {
                activity?.SetStatus(ActivityStatusCode.Error, ex.Message);

                // A failed call still took time and may still have been billed upstream, so the
                // duration is recorded either way; the token counts are not, because we did not
                // get them and inventing a zero-token success would understate the failure.
                metrics.AiCall(body.TenantId, resolved.Provider.ProviderId, resolved.Model,
                    0, 0, Stopwatch.GetElapsedTime(started));

                // Never include the request (which carries no secret) or the key in the message.
                logger.LogError(ex, "AI completion failed for provider {Provider}", resolved.Provider.ProviderId);
                return Results.Problem("AI completion failed.", statusCode: StatusCodes.Status502BadGateway);
            }
        }).RequireAuthorization();

        // The site chatbot's prompt overrides from the platform console. The gateway already reads
        // the tenant's AI settings for every call, so the bot (in content-api) asks here rather
        // than being given a grant on the ai schema. Only server-built prompts leave: the browser
        // agents read theirs from admin-api.
        app.MapGet("/v1/prompt-overrides", async (Guid tenantId, AiDbContext db, CancellationToken ct) =>
            tenantId == Guid.Empty
                ? Results.BadRequest(new { error = "tenantId is required." })
                : Results.Ok(await ServerPromptOverridesAsync(db, tenantId, ct)))
            .RequireAuthorization();

        return app;
    }

    /// <summary>
    /// A tenant's overrides of the server-built prompts, read as that tenant: the gateway is
    /// called service to service, so nothing ambient tells the database whose row this is.
    /// </summary>
    public static async Task<Dictionary<string, string>> ServerPromptOverridesAsync(
        AiDbContext db, Guid tenantId, CancellationToken ct)
    {
        using var rls = RlsScope.Tenant(tenantId);
        var json = await db.Settings.AsNoTracking()
            .Where(s => s.TenantId == tenantId)
            .Select(s => s.PromptOverridesJson)
            .FirstOrDefaultAsync(ct);
        return ServerPrompts.ParseOverrides(json)
            .Where(kv => ServerPrompts.Keys.Contains(kv.Key))
            .ToDictionary(kv => kv.Key, kv => kv.Value);
    }

    public sealed record ChatCompletionRequest(Guid TenantId, string Prompt, string? System, int? MaxTokens, Guid? UserId = null);
    public sealed record ChatCompletionResponse(string Provider, string Model, string Text);
}
