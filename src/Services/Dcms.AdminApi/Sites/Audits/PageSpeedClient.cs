using System.Text.Json;
using Microsoft.Extensions.Options;

namespace Dcms.AdminApi.Sites.Audits;

/// <summary>
/// Google PageSpeed Insights (#649). The key is required: Google gives keyless calls no quota.
/// Bound from <c>PageSpeed__ApiKey</c> (Vault); without one, audits say the platform is not set up.
/// </summary>
public sealed class PageSpeedOptions
{
    public const string Section = "PageSpeed";

    public string? ApiKey { get; set; }

    /// <summary>Overridable for tests.</summary>
    public string ApiBase { get; set; } = "https://www.googleapis.com/pagespeedonline/v5/";
}

public sealed class PageSpeedException(string message, int? status = null) : Exception(message)
{
    public int? Status { get; } = status;
}

public sealed class PageSpeedClient(HttpClient http, IOptions<PageSpeedOptions> options)
{
    public static readonly string[] Categories = ["performance", "accessibility", "best-practices", "seo"];

    public bool Configured => !string.IsNullOrWhiteSpace(options.Value.ApiKey);

    /// <summary>One run of every category for <paramref name="url"/>, as PSI answers it.</summary>
    public async Task<JsonDocument> RunAsync(string url, string strategy, CancellationToken ct)
    {
        var query = $"runPagespeed?url={Uri.EscapeDataString(url)}&strategy={strategy}"
            + string.Concat(Categories.Select(c => $"&category={c}"))
            + $"&key={Uri.EscapeDataString(options.Value.ApiKey ?? string.Empty)}";
        using var response = await http.GetAsync(query, ct);
        var body = await response.Content.ReadAsStringAsync(ct);
        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(body);
        }
        catch (JsonException)
        {
            throw new PageSpeedException($"PageSpeed Insights answered {(int)response.StatusCode} with no result.", (int)response.StatusCode);
        }
        if (!response.IsSuccessStatusCode || doc.RootElement.TryGetProperty("error", out _))
        {
            var message = doc.RootElement.TryGetProperty("error", out var error) && error.TryGetProperty("message", out var m)
                ? m.GetString() : null;
            doc.Dispose();
            // Google's own words: an unreachable page, a quota, a bad key — the person can act on each.
            throw new PageSpeedException($"PageSpeed Insights: {message ?? $"error {(int)response.StatusCode}"}", (int)response.StatusCode);
        }
        return doc;
    }
}
