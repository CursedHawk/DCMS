using System.Text.Json;
using System.Text.Json.Serialization;

namespace Dcms.Shared.Data.Sites;

/// <summary>
/// C# mirror of <c>site.json</c> — the Mode A manifest authored by the visual
/// builder (see <c>packages/gjs-schema/src/site.ts</c>, which is the source of
/// truth for the shape). It carries everything about a site that is not page
/// markup or CSS: theme tokens, the page index, navigation and document settings.
///
/// Version 2 is the file-map format (<c>pages/*.html</c> + <c>styles/*.css</c> in
/// a git repo). Version 1 was the component-tree definition; a v1 document does
/// not parse into this type, which is deliberate — a stale definition must fail
/// the build loudly rather than publish an empty site.
/// </summary>
public sealed class SiteManifest
{
    public const int CurrentVersion = 2;

    public int Version { get; set; } = CurrentVersion;
    public ThemeTokens Theme { get; set; } = new();
    public List<PageEntry> Pages { get; set; } = [];
    public List<NavItem> Nav { get; set; } = [];
    public SiteSettings Settings { get; set; } = new();

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
    };

    /// <summary>
    /// Parse a manifest, or null when the content is absent, malformed or a
    /// version this build does not understand. Callers decide what an unreadable
    /// manifest means; the assembler treats it as a build failure.
    /// </summary>
    public static SiteManifest? TryParse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }
        try
        {
            var manifest = JsonSerializer.Deserialize<SiteManifest>(json, JsonOptions);
            return manifest is { Version: CurrentVersion } ? manifest : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>The page a request for "/" resolves to: the flagged home, else the first.</summary>
    public PageEntry? HomePage() =>
        Pages.FirstOrDefault(p => p.Home) ?? Pages.FirstOrDefault(p => p.Path == "/") ?? Pages.FirstOrDefault();
}

public sealed class ThemeTokens
{
    public Dictionary<string, string> Colors { get; set; } = [];
    public Dictionary<string, string> Fonts { get; set; } = [];
    public Dictionary<string, string> Spacing { get; set; } = [];
    public string? Radius { get; set; }

    /// <summary>Raw custom properties merged into <c>:root</c> verbatim.</summary>
    public Dictionary<string, string> Custom { get; set; } = [];
}

public sealed class PageEntry
{
    public string Id { get; set; } = string.Empty;

    /// <summary>File-name stem: <c>pages/{Slug}.html</c>, <c>styles/pages/{Slug}.css</c>.</summary>
    public string Slug { get; set; } = string.Empty;

    /// <summary>Public route; the published file name is derived from it.</summary>
    public string Path { get; set; } = "/";

    public string Title { get; set; } = string.Empty;
    public SeoMeta Seo { get; set; } = new();

    /// <summary>True for the page served at the site root.</summary>
    public bool Home { get; set; }
}

public sealed class SeoMeta
{
    public string Title { get; set; } = string.Empty;
    public string? Description { get; set; }
    public string? OgImage { get; set; }
    public string? Canonical { get; set; }
    public bool NoIndex { get; set; }
}

public sealed class NavItem
{
    public string Label { get; set; } = string.Empty;
    public string Path { get; set; } = "/";
    public List<NavItem>? Children { get; set; }
}

public sealed class SiteSettings
{
    public string Lang { get; set; } = "en";
    public string? Favicon { get; set; }

    /// <summary>Markup appended to <c>&lt;head&gt;</c> (analytics, verification tags).</summary>
    public string? HeadHtml { get; set; }

    /// <summary>Markup appended before <c>&lt;/body&gt;</c>.</summary>
    public string? BodyEndHtml { get; set; }

    /// <summary>Render the manifest nav above each page's body.</summary>
    public bool RenderNav { get; set; } = true;
}
