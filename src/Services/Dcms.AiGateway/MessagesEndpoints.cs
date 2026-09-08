using System.Diagnostics;
using System.Text;
using System.Text.Json.Nodes;
using Dcms.AiGateway.Providers;
using Dcms.Shared.Data.Ai;
using Dcms.Shared.Telemetry;

namespace Dcms.AiGateway;

/// <summary>
/// Tool-use / streaming message relay for the browser agent — the web IDE's assistant and the
/// ⌘J dock. Called internally by admin-api with a dcms.ai client-credentials token. Resolves the
/// (tenant, user) credentials and injects the API key server-side, so it never reaches the
/// browser. The agent loop and its tools run in the browser; only model turns come through here.
///
/// <para><b>Any provider, not only Anthropic.</b> The browser speaks the Anthropic Messages
/// format, so an Anthropic upstream is a byte-for-byte proxy — the cheapest thing this can be,
/// and it stays that. Everything else goes through <see cref="AnthropicOpenAiBridge"/>, which
/// translates the request to OpenAI Chat Completions and the streamed answer back, so OpenAI,
/// Ollama, LM Studio and every OpenAI-compatible gateway drive the same agent loop.</para>
///
/// <para>This used to refuse outright unless the resolved provider was Anthropic, and to refuse
/// an empty key even for a local model that has none — which meant a workspace pointed at its
/// own Ollama was told to go and set up an Anthropic account.</para>
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

            // A hosted provider needs a key; a model running on the operator's own machine does
            // not, and demanding one was the reason a workspace pointed at Ollama was told to
            // set up an Anthropic account.
            if (RequiresApiKey(creds.Provider) && string.IsNullOrEmpty(creds.ApiKey))
            {
                return Results.Json(
                    new
                    {
                        error = "no_api_key",
                        provider = creds.Provider.ToString(),
                        message = $"No {Label(creds.Provider)} API key is configured for this user or tenant.",
                    },
                    statusCode: StatusCodes.Status402PaymentRequired);
            }

            var anthropicWire = creds.Provider is AiProvider.Anthropic;
            var payload = body.Request;
            payload["model"] ??= creds.Model;

            // The model that was actually asked for, after the fill-in above — the agent pins a
            // model per turn, so the resolver's default is often not what ran.
            var model = payload["model"]?.GetValue<string>() ?? creds.Model;

            var baseUrl = (creds.BaseUrl ?? DefaultFor(creds.Provider)).TrimEnd('/');
            var systemName = creds.Provider.ToString().ToLowerInvariant();

            using var upstreamRequest = anthropicWire
                ? Anthropic(baseUrl, payload, creds.ApiKey)
                : OpenAiCompatible(baseUrl, payload, model, creds);

            using var activity = DcmsActivitySource.Start("ai.messages.proxy", ActivityKind.Client);
            activity?.SetTag("gen_ai.system", systemName);
            activity?.SetTag("gen_ai.request.model", model);

            var started = Stopwatch.GetTimestamp();
            var http = httpClientFactory.CreateClient("ai-upstream");
            HttpResponseMessage upstream;
            try
            {
                upstream = await http.SendAsync(upstreamRequest, HttpCompletionOption.ResponseHeadersRead, ct);
            }
            catch (Exception ex)
            {
                activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
                metrics.AiCall(body.TenantId, systemName, model, 0, 0, Stopwatch.GetElapsedTime(started));
                logger.LogError(ex, "Upstream {Provider} request failed for tenant {TenantId}",
                    systemName, body.TenantId);
                return Results.Problem("AI request failed.", statusCode: StatusCodes.Status502BadGateway);
            }

            ctx.Response.StatusCode = (int)upstream.StatusCode;
            ctx.Features.Get<Microsoft.AspNetCore.Http.Features.IHttpResponseBodyFeature>()?.DisableBuffering();

            long promptTokens = 0;
            long completionTokens = 0;
            await using var upstreamStream = await upstream.Content.ReadAsStreamAsync(ct);
            try
            {
                if (anthropicWire)
                {
                    // A byte proxy, deliberately: the browser already speaks this format, and
                    // parsing a response only to re-emit it would be work for nothing. Tokens
                    // are read off the stream as it passes — this is the most expensive path in
                    // the platform, and without this the agent's spend would be the one thing
                    // the cost dashboard could not see.
                    ctx.Response.ContentType =
                        upstream.Content.Headers.ContentType?.ToString() ?? "application/json";
                    var usage = new AnthropicUsageScanner();
                    await usage.CopyAsync(upstreamStream, ctx.Response.Body, ct);
                    (promptTokens, completionTokens) = (usage.PromptTokens, usage.CompletionTokens);
                }
                else if (!upstream.IsSuccessStatusCode)
                {
                    // An upstream refusal is JSON in either dialect and the client reads it as
                    // JSON; passing it through unchanged keeps the provider's own message,
                    // which is the useful part of a 401 or a 429.
                    ctx.Response.ContentType =
                        upstream.Content.Headers.ContentType?.ToString() ?? "application/json";
                    await upstreamStream.CopyToAsync(ctx.Response.Body, ct);
                }
                else
                {
                    ctx.Response.ContentType = "text/event-stream";
                    var translator = new OpenAiStreamTranslator();
                    await TranslateAsync(upstreamStream, ctx.Response, translator, ct);
                    (promptTokens, completionTokens) = (translator.PromptTokens, translator.CompletionTokens);
                }
            }
            finally
            {
                // Recorded even when the browser disconnects mid-stream: the tokens produced up
                // to that point were still generated, and still billed.
                metrics.AiCall(body.TenantId, systemName, model,
                    promptTokens, completionTokens, Stopwatch.GetElapsedTime(started));
                activity?.SetTag("gen_ai.usage.input_tokens", promptTokens);
                activity?.SetTag("gen_ai.usage.output_tokens", completionTokens);
                upstream.Dispose();
            }
            return Results.Empty;
        }).RequireAuthorization();

        return app;
    }


    /// <summary>Reads the upstream OpenAI SSE stream and writes the Anthropic events for it.</summary>
    private static async Task TranslateAsync(
        Stream upstream, HttpResponse response, OpenAiStreamTranslator translator, CancellationToken ct)
    {
        using var reader = new StreamReader(upstream, Encoding.UTF8);

        while (await reader.ReadLineAsync(ct) is { } line)
        {
            if (!line.StartsWith("data:", StringComparison.Ordinal)) continue;

            foreach (var frame in translator.Feed(line[5..].Trim()))
            {
                await response.WriteAsync(frame, ct);
            }
            await response.Body.FlushAsync(ct);
        }

        // Always, including on a stream that ended badly: the agent loop parses a tool call's
        // accumulated JSON on `content_block_stop`, so without one it holds a call it never
        // dispatches and waits for a turn that has already finished.
        foreach (var frame in translator.Finish())
        {
            await response.WriteAsync(frame, ct);
        }
        await response.Body.FlushAsync(ct);
    }

    private static HttpRequestMessage Anthropic(string baseUrl, JsonObject payload, string apiKey)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, $"{baseUrl}/v1/messages")
        {
            Content = new StringContent(payload.ToJsonString(), Encoding.UTF8, "application/json"),
        };
        request.Headers.TryAddWithoutValidation("x-api-key", apiKey);
        request.Headers.TryAddWithoutValidation("anthropic-version", AnthropicVersion);
        return request;
    }

    private static HttpRequestMessage OpenAiCompatible(
        string baseUrl, JsonObject payload, string model, ResolvedCredentials creds)
    {
        var translated = AnthropicOpenAiBridge.TranslateRequest(
            payload, model, includeUsageOption: creds.Provider is AiProvider.OpenAi);

        var request = new HttpRequestMessage(HttpMethod.Post, $"{baseUrl}/chat/completions")
        {
            Content = new StringContent(translated.ToJsonString(), Encoding.UTF8, "application/json"),
        };
        // Sent even when empty for a local server, which ignores it — and never sent as an empty
        // header, which some gateways treat as a malformed credential rather than as none.
        if (!string.IsNullOrEmpty(creds.ApiKey))
        {
            request.Headers.TryAddWithoutValidation("Authorization", $"Bearer {creds.ApiKey}");
        }
        return request;
    }

    /// <summary>
    /// Whether a credential is required at all. Ollama and LM Studio run on the operator's own
    /// machine and authenticate nothing; insisting on a key for them is how "use your own local
    /// model" turned into "set up an Anthropic account".
    /// </summary>
    private static bool RequiresApiKey(AiProvider provider) =>
        provider is not (AiProvider.Ollama or AiProvider.LmStudio);

    private static string DefaultFor(AiProvider provider) => provider switch
    {
        AiProvider.Anthropic => DefaultBaseUrl,
        AiProvider.OpenAi => "https://api.openai.com/v1",
        AiProvider.Ollama => "http://localhost:11434/v1",
        AiProvider.LmStudio => "http://localhost:1234/v1",
        _ => DefaultBaseUrl,
    };

    private static string Label(AiProvider provider) => provider switch
    {
        AiProvider.Anthropic => "Anthropic",
        AiProvider.OpenAi => "OpenAI",
        AiProvider.Ollama => "Ollama",
        AiProvider.LmStudio => "LM Studio",
        _ => provider.ToString(),
    };

    /// <param name="Request">The raw Anthropic Messages API request body (messages, tools, system, max_tokens, stream, thinking, …). <c>model</c> is optional; the resolver fills it.</param>
    public sealed record MessagesProxyRequest(Guid TenantId, Guid? UserId, JsonObject Request);
}
