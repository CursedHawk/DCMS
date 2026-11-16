using System.Text.Json;

namespace Dcms.Shared.Data.Sites;

/// <summary>
/// What search engines may see of a site, set by its people (#648) on top of the sitemap the
/// platform makes (#343). site-host applies it on every request, so it needs no rebuild; it
/// travels with the site in deployments.
/// <list type="bullet">
/// <item><see cref="Indexable"/> false: every page answers noindex, robots.txt disallows all, no sitemap.</item>
/// <item><see cref="Exclude"/>: pages left out of the sitemap and answered noindex. <c>/about</c> is
/// that page; a <c>:name</c> segment matches any one segment (<c>/blog/:slug</c>, every post);
/// a final <c>/*</c> is the path and everything under it (<c>/private/*</c>).</item>
/// <item><see cref="Include"/>: pages the sitemap lists besides those it finds itself.</item>
/// </list>
/// </summary>
public sealed record SiteSearchSettings
{
    public const int MaxRules = 500;
    public const int MaxPathLength = 2048;

    public static readonly SiteSearchSettings Default = new();

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public bool Indexable { get; init; } = true;
    public IReadOnlyList<string> Exclude { get; init; } = [];
    public IReadOnlyList<string> Include { get; init; } = [];

    public bool IsDefault => Indexable && Exclude.Count == 0 && Include.Count == 0;

    /// <summary>The stored form; null for the defaults, so an untouched site stores nothing.</summary>
    public string? ToJson() => IsDefault ? null : JsonSerializer.Serialize(this, Json);

    /// <summary>The settings a site stored; the defaults for none, or for anything unreadable.</summary>
    public static SiteSearchSettings Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return Default;
        }
        try
        {
            var settings = JsonSerializer.Deserialize<SiteSearchSettings>(json, Json) ?? Default;
            return settings with { Exclude = settings.Exclude ?? [], Include = settings.Include ?? [] };
        }
        catch (JsonException)
        {
            return Default;
        }
    }

    /// <summary>Whether an exclude rule leaves this page out (and answers it noindex).</summary>
    public bool Excludes(string path) => Exclude.Any(rule => Matches(rule, path));

    /// <summary>Whether <paramref name="rule"/> covers <paramref name="path"/>, compared without case.</summary>
    public static bool Matches(string rule, string path)
    {
        var r = rule.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var p = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var section = r.Length > 0 && r[^1] == "*";
        if (section)
        {
            r = r[..^1];
            if (p.Length < r.Length) return false;
        }
        else if (p.Length != r.Length)
        {
            return false;
        }
        for (var i = 0; i < r.Length; i++)
        {
            if (!r[i].StartsWith(':') && !string.Equals(r[i], p[i], StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }
        return true;
    }

    /// <summary>What is wrong with settings someone sent; empty when they may be stored.</summary>
    public static IReadOnlyList<string> Validate(SiteSearchSettings settings)
    {
        var errors = new List<string>();
        if (settings.Exclude.Count > MaxRules) errors.Add($"Hide at most {MaxRules} paths.");
        if (settings.Include.Count > MaxRules) errors.Add($"Add at most {MaxRules} extra URLs.");
        foreach (var rule in settings.Exclude.Take(MaxRules))
        {
            if (Problem(rule, patterns: true) is { } problem) errors.Add($"Hidden path \"{Clip(rule)}\" {problem}.");
        }
        foreach (var path in settings.Include.Take(MaxRules))
        {
            if (Problem(path, patterns: false) is { } problem) errors.Add($"Extra URL \"{Clip(path)}\" {problem}.");
        }
        return errors;
    }

    private static string? Problem(string? path, bool patterns)
    {
        if (string.IsNullOrEmpty(path)) return "is empty";
        if (path.Length > MaxPathLength) return $"is longer than {MaxPathLength} characters";
        if (path[0] != '/') return "must start with /";
        if (path.Any(c => char.IsControl(c) || char.IsWhiteSpace(c))) return "cannot hold spaces or control characters";
        if (path.IndexOfAny(['?', '#']) >= 0) return "cannot hold ? or #";
        if (path == "/") return null;
        var segments = path[1..].Split('/');
        for (var i = 0; i < segments.Length; i++)
        {
            var segment = segments[i];
            if (segment is "" or "." or "..") return "has an empty, . or .. part";
            if (segment.Contains('*') && !(patterns && segment == "*" && i == segments.Length - 1))
            {
                return patterns ? "may use * only at the end, as /*" : "cannot hold *";
            }
            if (segment.StartsWith(':') && !patterns) return "cannot hold a :parameter";
        }
        return null;
    }

    private static string Clip(string? value) => value is null ? "" : value.Length <= 60 ? value : value[..60] + "…";
}
