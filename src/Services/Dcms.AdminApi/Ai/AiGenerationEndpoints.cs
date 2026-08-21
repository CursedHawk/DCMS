using Dcms.Shared.Audit;
using Dcms.Shared.Audit.Http;
using System.Net.Http.Json;
using System.Text.Json;
using Dcms.Shared.Kernel.Abstractions;
using Dcms.Shared.Security;

namespace Dcms.AdminApi.Ai;

/// <summary>
/// AI-assisted generation for the Mode A visual builder.
///
/// Assembles a system prompt from the caller's component catalogue, the site's
/// theme variables and the tenant's enabled plugin instances (see
/// <see cref="AiPromptBuilder"/>), relays it to ai-gateway with a
/// client-credentials token, and returns the model's JSON.
///
/// Three flows, differing only in what is asked for: a block to insert, a whole
/// page, or a whole site. Nothing is written here — the builder validates the
/// output and applies it through the same working-draft path a hand edit takes,
/// so an AI change is an ordinary uncommitted diff the author can review, undo
/// or throw away in the Source Control panel.
/// </summary>
public static class AiGenerationEndpoints
{
    private const string AiScope = "dcms.ai";

    public static IEndpointRouteBuilder MapAiGenerationEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/api/admin/ai/generate/block", (
            GenerateBlockRequest body, AiPromptBuilder prompts, IServiceTokenProvider tokens,
            IHttpClientFactory httpClientFactory, ITenantContext tenant, CancellationToken ct) =>
            GenerateAsync(prompts, tokens, httpClientFactory, tenant, body,
                framing: """
                    Generate ONE self-contained section to insert into an existing page.
                    Respond with: {"html": "<the fragment>", "css": "<rules for its classes>"}
                    The CSS must style only the classes this fragment introduces; do not
                    restyle anything else on the page.
                    """,
                userPrompt: WithContext(body.Instruction, "The page it will be inserted into", body.Context),
                ct))
            .RequirePermission(PlatformPermissions.SiteEdit)
            .WithAudit(AuditActions.AiGenerateBlock, "site");

        app.MapPost("/api/admin/ai/generate/page", (
            GeneratePageRequest body, AiPromptBuilder prompts, IServiceTokenProvider tokens,
            IHttpClientFactory httpClientFactory, ITenantContext tenant, CancellationToken ct) =>
            GenerateAsync(prompts, tokens, httpClientFactory, tenant, body,
                framing: """
                    Generate a complete page body: several stacked sections that together
                    make a finished page.
                    Respond with: {"html": "<the page body>", "css": "<rules for its classes>"}
                    """,
                userPrompt: WithContext(body.Instruction, "The page as it is now, which you are replacing", body.Context),
                ct))
            .RequirePermission(PlatformPermissions.SiteEdit)
            .WithAudit(AuditActions.AiGeneratePage, "site");

        app.MapPost("/api/admin/ai/generate/site", (
            GenerateSiteRequest body, AiPromptBuilder prompts, IServiceTokenProvider tokens,
            IHttpClientFactory httpClientFactory, ITenantContext tenant, CancellationToken ct) =>
            GenerateAsync(prompts, tokens, httpClientFactory, tenant, body,
                framing: SiteFraming,
                userPrompt: body.Instruction,
                ct))
            .RequirePermission(PlatformPermissions.SiteEdit)
            .WithAudit(AuditActions.AiGenerateSite, "site");

        return app;
    }

    /// <summary>
    /// The whole-site flow returns a file map, which is exactly what the builder
    /// already stores — so applying it is the same write the canvas performs, and
    /// no separate "AI project" representation exists to keep in step.
    /// </summary>
    private const string SiteFraming = """
        Generate a complete small website: a sitemap of 3–6 pages, each with real
        content for the brief.

        Respond with: {"files": { "<path>": "<file contents>", … }} containing:
          - "site.json": {"version":2,
                          "theme":{"colors":{"primary":"#…","surface":"#…","text":"#…","muted":"#…","border":"#…"},
                                   "fonts":{"body":"…","heading":"…"},"radius":"…"},
                          "nav":[{"label":"…","path":"/…"}],
                          "pages":[{"id":"…","slug":"…","path":"/…","title":"…","seo":{"title":"…","description":"…"}}],
                          "settings":{}}
          - "pages/<slug>.html" for every page in the manifest — its body fragment
          - "styles/global.css" — shared rules
          - "styles/pages/<slug>.css" for page-specific rules (may be empty)
        The home page must have slug "home" and path "/". Every page listed in
        site.json must have a matching pages/<slug>.html file, and vice versa.
        Do not emit "styles/theme.css"; it is generated from site.json.
        """;

    private static string WithContext(string instruction, string label, string? context)
    {
        return string.IsNullOrWhiteSpace(context)
            ? instruction
            : $"{instruction}\n\n{label}:\n{Clip(context)}";
    }

    /// <summary>The current page is context, not the subject; it must not crowd out the request.</summary>
    private static string Clip(string value) => value.Length > 12_000 ? value[..12_000] + "\n…" : value;

    private static async Task<IResult> GenerateAsync(
        AiPromptBuilder prompts, IServiceTokenProvider tokens, IHttpClientFactory httpClientFactory,
        ITenantContext tenant, IGenerationRequest body, string framing, string userPrompt,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(userPrompt))
        {
            return Results.BadRequest(new { error = "An instruction is required." });
        }

        var system = await prompts.BuildSystemPromptAsync(body.Components, body.ThemeVariables, ct)
            + "\n\n" + framing;

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

        // Best-effort: return parsed JSON when valid, else the raw text. The
        // builder shows the raw output rather than silently discarding a result
        // the author waited for.
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

    /// <summary>What every flow needs to build a prompt that fits this tenant's site.</summary>
    private interface IGenerationRequest
    {
        /// <summary>The builder's component catalogue, including generated plugin components.</summary>
        ComponentHint[]? Components { get; }
        /// <summary>Custom properties the theme defines, e.g. `--dcms-color-primary`.</summary>
        string[]? ThemeVariables { get; }
    }

    private sealed record GenerateBlockRequest(
        string Instruction, string? Context, ComponentHint[]? Components, string[]? ThemeVariables)
        : IGenerationRequest;

    private sealed record GeneratePageRequest(
        string Instruction, string? Context, ComponentHint[]? Components, string[]? ThemeVariables)
        : IGenerationRequest;

    private sealed record GenerateSiteRequest(
        string Instruction, ComponentHint[]? Components, string[]? ThemeVariables)
        : IGenerationRequest;
}
