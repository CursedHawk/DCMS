using System.Diagnostics;
using System.Text;
using System.Text.Json.Nodes;
using Dcms.AiGateway.Providers;
using Dcms.Shared.Data.Ai;
using Dcms.Shared.Telemetry;

namespace Dcms.AiGateway;

/// <summary>
/// Tool-use / streaming message relay for the web-IDE agent. Called internally by
/// admin-api with a dcms.ai client-credentials token. Resolves the (tenant, user)
/// Anthropic credentials and reverse-proxies a raw Anthropic Messages request —
/// injecting the API key server-side so it never reaches the browser — streaming
/// the (SSE) response straight back. The agent loop and its file tools run in the
/// browser against the live VFS; only model turns come through here.
/// </summary>
public static class MessagesEndpoints
{
    private const string AnthropicVersion = "2023-06-01";
    private const string DefaultBaseUrl = "https://api.anthropic.com";

    public static IEndpointRouteBuilder MapMessagesEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/v1/messages", async (
            MessagesProxyRequest body, HttpContext ctx, AiProviderResolver resolver,
            IHttpClientFactory httpClientFactory, DcmsMetrics metrics,
            ILoggerFactory loggerFactory, CancellationToken ct) =>
        {
            var logger = loggerFactory.CreateLogger("ai-gateway.messages");

            if (body.TenantId == Guid.Empty || body.Request is null)
            {
                return Results.BadRequest(new { error = "tenantId and request are required." });
            }

            ResolvedCredentials creds;
            try
            {
                creds = await resolver.ResolveCredentialsAsync(body.TenantId, body.UserId, ct);
            }
            catch (AiCredentialScopeException ex)
            {
                // A settings problem the caller can fix, and the resolver has already recorded
                // the refusal — so say what is wrong rather than returning an opaque 500.
                return Results.BadRequest(new { error = "credential_scope", message = ex.Message });
            }

            if (creds.Provider != AiProvider.Anthropic)
            {
                // The IDE agent is Claude-specific (Anthropic tool-use wire format).
                return Results.BadRequest(new { error = "The IDE agent requires an Anthropic provider/key." });
            }
            if (string.IsNullOrEmpty(creds.ApiKey))
            {
                return Results.Json(new { error = "no_api_key", message = "No Anthropic API key is configured for this user or tenant." },
                    statusCode: StatusCodes.Status402PaymentRequired);
            }

            // Pass the Anthropic request through verbatim, filling in the resolved model
            // when the caller didn't pin one.
            var payload = body.Request;
            payload["model"] ??= creds.Model;

            var baseUrl = (creds.BaseUrl ?? DefaultBaseUrl).TrimEnd('/');
            using var upstreamRequest = new HttpRequestMessage(HttpMethod.Post, $"{baseUrl}/v1/messages")
            {
                Content = new StringContent(payload.ToJsonString(), Encoding.UTF8, "application/json"),
            };
            upstreamRequest.Headers.TryAddWithoutValidation("x-api-key", creds.ApiKey);
            upstreamRequest.Headers.TryAddWithoutValidation("anthropic-version", AnthropicVersion);

            // The model that was actually asked for, after the fill-in above — the agent pins a
            // model per turn, so the resolver's default is often not what ran.
            var model = payload["model"]?.GetValue<string>() ?? creds.Model;

            using var activity = DcmsActivitySource.Start("ai.messages.proxy", ActivityKind.Client);
            activity?.SetTag("gen_ai.system", "anthropic");
            activity?.SetTag("gen_ai.request.model", model);

            var started = Stopwatch.GetTimestamp();
            var http = httpClientFactory.CreateClient("anthropic");
            HttpResponseMessage upstream;
            try
            {
                upstream = await http.SendAsync(upstreamRequest, HttpCompletionOption.ResponseHeadersRead, ct);
            }
            catch (Exception ex)
            {
                activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
                metrics.AiCall(body.TenantId, "anthropic", model, 0, 0, Stopwatch.GetElapsedTime(started));
                logger.LogError(ex, "Upstream Anthropic request failed for tenant {TenantId}", body.TenantId);
                return Results.Problem("AI request failed.", statusCode: StatusCodes.Status502BadGateway);
            }

            // Stream the response (SSE or JSON) straight through, preserving status.
            ctx.Response.StatusCode = (int)upstream.StatusCode;
            ctx.Response.ContentType = upstream.Content.Headers.ContentType?.ToString() ?? "application/json";
            ctx.Features.Get<Microsoft.AspNetCore.Http.Features.IHttpResponseBodyFeature>()?.DisableBuffering();

            // Tokens are read off the stream as it passes rather than from a parsed response,
            // because there is no parsed response here — this endpoint is a byte proxy, and it
            // is also the single most expensive path in the platform. Without this the IDE
            // agent's spend would be the one thing the cost dashboard could not see.
            var usage = new AnthropicUsageScanner();
            await using var upstreamStream = await upstream.Content.ReadAsStreamAsync(ct);
            try
            {
                await usage.CopyAsync(upstreamStream, ctx.Response.Body, ct);
            }
            finally
            {
                // Recorded even when the browser disconnects mid-stream: the tokens produced up
                // to that point were still generated, and still billed.
                metrics.AiCall(body.TenantId, "anthropic", model,
                    usage.PromptTokens, usage.CompletionTokens, Stopwatch.GetElapsedTime(started));
                activity?.SetTag("gen_ai.usage.input_tokens", usage.PromptTokens);
                activity?.SetTag("gen_ai.usage.output_tokens", usage.CompletionTokens);
                upstream.Dispose();
            }
            return Results.Empty;
        }).RequireAuthorization();

        return app;
    }

    /// <param name="Request">The raw Anthropic Messages API request body (messages, tools, system, max_tokens, stream, thinking, …). <c>model</c> is optional; the resolver fills it.</param>
    public sealed record MessagesProxyRequest(Guid TenantId, Guid? UserId, JsonObject Request);
}
