using System.Diagnostics;
using Dcms.AiGateway.Providers;
using Dcms.Shared.Telemetry;

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
            ILoggerFactory loggerFactory, CancellationToken ct) =>
        {
            if (body.TenantId == Guid.Empty || string.IsNullOrWhiteSpace(body.Prompt))
            {
                return Results.BadRequest(new { error = "tenantId and prompt are required." });
            }

            var resolved = await resolver.ResolveAsync(body.TenantId, body.UserId, ct);
            var logger = loggerFactory.CreateLogger("ai-gateway");
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

        return app;
    }

    public sealed record ChatCompletionRequest(Guid TenantId, string Prompt, string? System, int? MaxTokens, Guid? UserId = null);
    public sealed record ChatCompletionResponse(string Provider, string Model, string Text);
}
