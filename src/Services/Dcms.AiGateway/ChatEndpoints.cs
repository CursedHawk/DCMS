using Dcms.AiGateway.Providers;

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
            ChatCompletionRequest body, AiProviderResolver resolver,
            ILoggerFactory loggerFactory, CancellationToken ct) =>
        {
            if (body.TenantId == Guid.Empty || string.IsNullOrWhiteSpace(body.Prompt))
            {
                return Results.BadRequest(new { error = "tenantId and prompt are required." });
            }

            var resolved = await resolver.ResolveAsync(body.TenantId, ct);
            var logger = loggerFactory.CreateLogger("ai-gateway");
            logger.LogInformation("Chat completion via {Provider}/{Model} for tenant {TenantId}",
                resolved.Provider.ProviderId, resolved.Model, body.TenantId);

            try
            {
                var text = await resolved.Provider.CompleteAsync(
                    new ChatRequest(resolved.Model, body.System, body.Prompt, body.MaxTokens ?? 8192), ct);
                return Results.Ok(new ChatCompletionResponse(resolved.Provider.ProviderId, resolved.Model, text));
            }
            catch (Exception ex)
            {
                // Never include the request (which carries no secret) or the key in the message.
                logger.LogError(ex, "AI completion failed for provider {Provider}", resolved.Provider.ProviderId);
                return Results.Problem("AI completion failed.", statusCode: StatusCodes.Status502BadGateway);
            }
        }).RequireAuthorization();

        return app;
    }

    public sealed record ChatCompletionRequest(Guid TenantId, string Prompt, string? System, int? MaxTokens);
    public sealed record ChatCompletionResponse(string Provider, string Model, string Text);
}
