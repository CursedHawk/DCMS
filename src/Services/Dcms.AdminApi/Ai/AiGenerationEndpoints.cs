using System.Net.Http.Json;
using System.Text.Json;
using Dcms.Shared.Kernel.Abstractions;
using Dcms.Shared.Security;

namespace Dcms.AdminApi.Ai;

/// <summary>
/// AI-assisted editor generation. Assembles a prompt from the component registry
/// and the tenant's enabled plugin instances, relays it to ai-gateway via a
/// client-credentials token, and returns the model's JSON (the editor parses and
/// validates it before applying). Component-level and full-site flows differ only
/// in the framing instruction.
/// </summary>
public static class AiGenerationEndpoints
{
    private const string AiScope = "dcms.ai";

    public static IEndpointRouteBuilder MapAiGenerationEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/api/admin/ai/generate/component", (
            GenerateComponentRequest body, AiPromptBuilder prompts, IServiceTokenProvider tokens,
            IHttpClientFactory httpClientFactory, ITenantContext tenant, CancellationToken ct) =>
            GenerateAsync(prompts, tokens, httpClientFactory, tenant,
                framing: "Generate a single ComponentNode (with children) for this instruction. Output one ComponentNode JSON object.",
                userPrompt: body.Instruction + (body.CurrentSubtree is { } s ? $"\n\nCurrent subtree:\n{s}" : string.Empty),
                ct))
            .RequirePermission(PlatformPermissions.SiteEdit);

        app.MapPost("/api/admin/ai/generate/site", (
            GenerateSiteRequest body, AiPromptBuilder prompts, IServiceTokenProvider tokens,
            IHttpClientFactory httpClientFactory, ITenantContext tenant, CancellationToken ct) =>
            GenerateAsync(prompts, tokens, httpClientFactory, tenant,
                framing: "Generate a complete SiteDefinition for this brief, with a sensible sitemap and pages. Output one SiteDefinition JSON object.",
                userPrompt: body.Brief,
                ct))
            .RequirePermission(PlatformPermissions.SiteEdit);

        return app;
    }

    private static async Task<IResult> GenerateAsync(
        AiPromptBuilder prompts, IServiceTokenProvider tokens, IHttpClientFactory httpClientFactory,
        ITenantContext tenant, string framing, string userPrompt, CancellationToken ct)
    {
        var system = await prompts.BuildSystemPromptAsync(ct) + "\n\n" + framing;

        var token = await tokens.GetTokenAsync(AiScope, ct);
        var client = httpClientFactory.CreateClient("ai-gateway");
        client.DefaultRequestHeaders.Authorization = new("Bearer", token);

        var response = await client.PostAsJsonAsync("/v1/chat", new
        {
            tenantId = tenant.TenantId!.Value,
            prompt = userPrompt,
            system,
            maxTokens = 16000,
        }, ct);

        if (!response.IsSuccessStatusCode)
        {
            return Results.Problem("AI generation failed.", statusCode: StatusCodes.Status502BadGateway);
        }

        var payload = await response.Content.ReadFromJsonAsync<JsonElement>(ct);
        var text = payload.TryGetProperty("text", out var t) ? t.GetString() ?? string.Empty : string.Empty;

        // Best-effort: return parsed JSON when valid, else the raw text for the
        // client to repair. A server-side repair pass can be layered on later.
        var json = ExtractJson(text);
        if (json is not null && TryParse(json, out var parsed))
        {
            return Results.Ok(new { valid = true, result = parsed });
        }
        return Results.Ok(new { valid = false, raw = text });
    }

    // Strips accidental markdown fences and isolates the outermost JSON object.
    private static string? ExtractJson(string text)
    {
        var start = text.IndexOf('{');
        var end = text.LastIndexOf('}');
        return start >= 0 && end > start ? text[start..(end + 1)] : null;
    }

    private static bool TryParse(string json, out JsonElement element)
    {
        try
        {
            element = JsonDocument.Parse(json).RootElement.Clone();
            return true;
        }
        catch (JsonException)
        {
            element = default;
            return false;
        }
    }

    private sealed record GenerateComponentRequest(string Instruction, string? CurrentSubtree);
    private sealed record GenerateSiteRequest(string Brief);
}
