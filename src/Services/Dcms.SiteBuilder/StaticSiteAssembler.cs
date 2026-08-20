using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Dcms.Shared.Data.Sites;
using Dcms.Shared.Storage;

namespace Dcms.SiteBuilder;

public sealed record RenderedPage(string FileName, string Html);

/// <summary>One repo file copied to the build verbatim (stylesheets, committed assets).</summary>
public sealed record CopiedFile(string FileName, byte[] Content, string ContentType);

public sealed record AssembledSite(IReadOnlyList<RenderedPage> Pages, IReadOnlyList<CopiedFile> Files);

/// <summary>
/// Mode A assembler: turns the builder's committed source — <c>site.json</c>,
/// <c>pages/*.html</c>, <c>styles/*.css</c> — into the static site that gets
/// uploaded to object storage.
///
/// The page markup is authored by the visual builder (or by hand in its code
/// view) and is emitted <b>verbatim</b>; this class only wraps it in a document
/// shell: head/SEO metadata from the manifest, the stylesheet links in cascade
/// order, the navigation, and the hydration script tag. That is the whole point
/// of storing HTML rather than a component tree — what the author sees is what
/// ships, and there is no second renderer to keep in sync with the editor.
///
/// Data-bound and plugin components arrive already serialized as
/// <c>data-dcms-component</c> placeholders, which <c>/_dcms/hydrate.js</c> fills
/// in on the client, so this class needs no knowledge of plugins at all.
///
/// No Node dependency: a Mode A publish is pure string assembly.
/// </summary>
public sealed class StaticSiteAssembler
{
    public const string SiteJson = "site.json";
    public const string PagesPrefix = "pages/";
    public const string RegionsPrefix = "regions/";
    public const string BlocksPrefix = "blocks/";
    public const string StylesPrefix = "styles/";
    public const string ThemeCss = "styles/theme.css";
    public const string GlobalCss = "styles/global.css";
    public const string HydrateScript = "/_dcms/hydrate.js";

    /// <summary>
    /// The id of the embedded component registry. <c>hydrate.js</c> reads the
    /// tenant's own component templates out of it, so a component the author
    /// built renders without a second request — and without the delivery API
    /// having to know that tenant components exist at all.
    /// </summary>
    public const string ComponentRegistryId = "dcms-components";

    /// <summary>
    /// The id of the embedded route table. <c>hydrate.js</c> reads it to label a
    /// breadcrumb trail and to mark the current link in a menu — both of which
    /// live in a shared region and are therefore identical markup on every page,
    /// resolvable only against the URL the visitor actually asked for.
    /// </summary>
    public const string RouteTableId = "dcms-routes";

    /// <summary>
    /// The id of the embedded cookie-consent policy. <c>hydrate.js</c> reads it to
    /// decide whether to ask before storing anything for analytics, and what to say.
    ///
    /// Embedded in the page rather than fetched: the runtime has to know the answer
    /// *before* it may write the session id or fire the first beacon, and a request
    /// to find out would either delay every pageview or race it.
    /// </summary>
    public const string ConsentConfigId = "dcms-consent";

    /// <summary>
    /// camelCase and no nulls, so the embedded policy matches the shape the author
    /// edits in site.json and unset fields cost nothing on every page.
    /// </summary>
    private static readonly JsonSerializerOptions ConsentJson = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>Files that describe the site rather than being part of its output.</summary>
    /// <summary>
    /// Files consumed or kept private rather than published. `AGENTS.md` is the
    /// generated authoring contract for AI agents working on the repo — useful in
    /// git, but it describes the site's internals and has no business being served
    /// at a guessable URL.
    /// </summary>
    private static readonly HashSet<string> SourceOnly =
        new(StringComparer.Ordinal) { SiteJson, "assets.json", "AGENTS.md" };

    /// <summary>
    /// Directories consumed here rather than copied. Page and region markup is
    /// assembled into documents, and a component definition is embedded in every
    /// page that might use it — publishing any of them as files would expose the
    /// source and serve markup fragments at guessable URLs.
    /// </summary>
    private static readonly string[] SourceOnlyPrefixes = [PagesPrefix, RegionsPrefix, BlocksPrefix];

    /// <param name="analyticsEnabled">
    /// Whether the owning tenant records analytics. Baked into each page's consent
    /// block: a site that stores nothing must not show a cookie banner.
    /// </param>
    public AssembledSite Assemble(IReadOnlyDictionary<string, string> files, bool analyticsEnabled = true)
    {
        var manifest = SiteManifest.TryParse(files.GetValueOrDefault(SiteJson))
            ?? throw new InvalidOperationException(
                $"'{SiteJson}' is missing, malformed, or not version {SiteManifest.CurrentVersion}. " +
                "Open the site in the builder and save it, then publish again.");

        if (manifest.Pages.Count == 0)
        {
            throw new InvalidOperationException("This site has no pages. Add a page in the builder, then publish again.");
        }

        var home = manifest.HomePage();
        var pages = new List<RenderedPage>();
        var emitted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var registry = ComponentRegistry(files);
        var routes = RouteTable(manifest);

        foreach (var page in manifest.Pages)
        {
            var body = files.GetValueOrDefault(PageHtmlPath(page.Slug), string.Empty);
            var html = Document(manifest, page, body, files, registry, routes, analyticsEnabled);

            var fileName = FileNameFor(page.Path);
            // Two pages routed to the same path would silently overwrite each other's
            // artifact; keep the first and disambiguate the rest by slug so nothing
            // published is lost without a trace.
            if (!emitted.Add(fileName))
            {
                fileName = $"{page.Slug}.html";
                if (!emitted.Add(fileName)) continue;
            }
            pages.Add(new RenderedPage(fileName, html));

            // A home page not already served from index.html is also published there,
            // so the site root resolves without relying on the host's routing rules.
            if (page == home && !string.Equals(fileName, "index.html", StringComparison.OrdinalIgnoreCase) &&
                emitted.Add("index.html"))
            {
                pages.Add(new RenderedPage("index.html", html));
            }
        }

        return new AssembledSite(pages, CopiedFiles(files));
    }

    /// <summary>
    /// Everything in the repo that ships as-is: the stylesheets and any assets the
    /// author committed. Page markup and the manifest are consumed above, so they
    /// are excluded — publishing `site.json` would expose the source needlessly.
    /// </summary>
    private static List<CopiedFile> CopiedFiles(IReadOnlyDictionary<string, string> files)
    {
        var copied = new List<CopiedFile>();
        foreach (var (path, content) in files)
        {
            if (SourceOnly.Contains(path)) continue;
            if (SourceOnlyPrefixes.Any(p => path.StartsWith(p, StringComparison.Ordinal))) continue;
            if (!SiteFileMap.IsSafePath(path)) continue;

            var bytes = SiteFileMap.IsBinaryPath(path)
                ? DecodeBase64(path, content)
                : Encoding.UTF8.GetBytes(content);
            copied.Add(new CopiedFile(path, bytes, StaticSiteFiles.ContentTypeFor(path)));
        }
        return copied;
    }

    private static byte[] DecodeBase64(string path, string content)
    {
        try
        {
            return Convert.FromBase64String(content);
        }
        catch (FormatException)
        {
            throw new InvalidOperationException(
                $"Binary file '{path}' is not valid base64. Re-upload it in the site editor.");
        }
    }

    /// <summary>
    /// What a <c>:param</c> route segment becomes in a published file name.
    ///
    /// <c>@</c> because a real segment is kebab-case and can never contain one,
    /// so <see cref="SiteHost"/> can tell "the page for /events/anything" apart
    /// from "the page for /events/at". Mirrors WILDCARD_FILE_SEGMENT in
    /// @dcms/gjs-schema's paths module.
    /// </summary>
    public const string WildcardSegment = "@";

    /// <summary>
    /// True for a route carrying a <c>:param</c> segment — a detail route, one
    /// page serving every item under it (<c>/events/:slug</c>).
    /// </summary>
    public static bool IsDetailRoute(string path) =>
        path.Split('/').Any(segment => segment.StartsWith(':'));

    /// <summary>
    /// The published file name for a route. Unchanged for ordinary routes, so
    /// existing links and host rewrite rules keep working: <c>/about/team</c> →
    /// <c>about_team.html</c>, <c>/events/:slug</c> → <c>events_@.html</c>.
    /// </summary>
    public static string FileNameFor(string path)
    {
        var trimmed = path.Trim('/');
        if (string.IsNullOrEmpty(trimmed))
        {
            return "index.html";
        }
        var segments = trimmed.Split('/')
            .Select(segment => segment.StartsWith(':') ? WildcardSegment : segment);
        return $"{string.Join('_', segments)}.html";
    }

    public static string PageHtmlPath(string slug) => $"{PagesPrefix}{slug}.html";

    public static string RegionHtmlPath(string slug) => $"{RegionsPrefix}{slug}.html";

    public static string PageCssPath(string slug) => $"{StylesPrefix}pages/{slug}.css";

    /// <summary>
    /// The tenant's own component definitions, keyed by name, as one JSON object.
    ///
    /// Embedded in the page rather than fetched: a component the author built is
    /// part of the page's markup in every sense except that its data arrives
    /// late, and making the *layout* wait on a second round trip would give every
    /// such component a visible pop-in that a built-in one does not have. The
    /// definitions are small — a template and its prop list — and they compress
    /// with the document.
    ///
    /// A definition that is not valid JSON is skipped rather than failing the
    /// build: these files are hand-editable in the code view, and one broken
    /// component should cost that component, not the whole site.
    /// </summary>
    private static string ComponentRegistry(IReadOnlyDictionary<string, string> files)
    {
        var entries = new Dictionary<string, JsonNode?>(StringComparer.Ordinal);
        foreach (var (path, content) in files)
        {
            if (!path.StartsWith(BlocksPrefix, StringComparison.Ordinal)) continue;
            if (!path.EndsWith(".json", StringComparison.OrdinalIgnoreCase)) continue;

            var name = path[BlocksPrefix.Length..^".json".Length];
            if (name.Length == 0 || name.Contains('/')) continue;

            try
            {
                var node = JsonNode.Parse(content);
                // A definition with no template renders nothing, so carrying it
                // would only put an empty entry in every page of the site.
                if (node?["template"] is not null) entries[name] = node;
            }
            catch (JsonException)
            {
                // Skipped — see above.
            }
        }

        return entries.Count == 0 ? string.Empty : JsonSerializer.Serialize(entries);
    }

    /// <summary>
    /// Every page's path and title, as JSON. Small enough to embed in each
    /// document (a site with two hundred pages is a few kilobytes) and embedding
    /// it keeps navigation working on a static host with no API in front of it.
    /// </summary>
    /// <summary>
    /// The routes the page-aware navigation resolves against: breadcrumb titles
    /// and "you are here" highlighting.
    ///
    /// Detail routes are left out on purpose — <c>/events/:slug</c> is not an
    /// address, so it can never match the page being viewed, and a menu offering
    /// it would link to a literal colon. The breadcrumb for a real item URL
    /// falls back to humanising its last segment, which reads correctly.
    /// </summary>
    private static string RouteTable(SiteManifest manifest) =>
        JsonSerializer.Serialize(manifest.Pages
            .Where(p => !IsDetailRoute(p.Path))
            .Select(p => new { path = p.Path, title = p.Title }));

    private static string Document(
        SiteManifest manifest,
        PageEntry page,
        string body,
        IReadOnlyDictionary<string, string> files,
        string registry,
        string routes,
        bool analyticsEnabled)
    {
        var head = new StringBuilder();
        var settings = manifest.Settings;

        head.Append(CultureInfo.InvariantCulture, $"<title>{Text(page.Seo.Title)}</title>");
        if (!string.IsNullOrWhiteSpace(page.Seo.Description))
        {
            head.Append(CultureInfo.InvariantCulture,
                $"<meta name=\"description\" content=\"{Attr(page.Seo.Description!)}\" />");
        }
        if (page.Seo.NoIndex)
        {
            head.Append("<meta name=\"robots\" content=\"noindex\" />");
        }
        if (!string.IsNullOrWhiteSpace(page.Seo.Canonical))
        {
            head.Append(CultureInfo.InvariantCulture,
                $"<link rel=\"canonical\" href=\"{Attr(page.Seo.Canonical!)}\" />");
        }
        head.Append(CultureInfo.InvariantCulture,
            $"<meta property=\"og:title\" content=\"{Attr(page.Seo.Title)}\" />");
        if (!string.IsNullOrWhiteSpace(page.Seo.OgImage))
        {
            head.Append(CultureInfo.InvariantCulture,
                $"<meta property=\"og:image\" content=\"{Attr(page.Seo.OgImage!)}\" />");
        }
        if (!string.IsNullOrWhiteSpace(settings.Favicon))
        {
            head.Append(CultureInfo.InvariantCulture,
                $"<link rel=\"icon\" href=\"{Attr(settings.Favicon!)}\" />");
        }

        // Cascade order matters: theme variables, then shared rules, then the
        // page's own — the same order the builder's canvas loads them in.
        foreach (var sheet in new[] { ThemeCss, GlobalCss, PageCssPath(page.Slug) })
        {
            head.Append(CultureInfo.InvariantCulture, $"<link rel=\"stylesheet\" href=\"/{sheet}\" />");
        }

        // Author-supplied head markup is emitted raw (analytics snippets need to be).
        // A Mode A repo is tenant-authored source, the same trust level a Mode B site
        // already has, so it is not sanitized here.
        if (!string.IsNullOrWhiteSpace(settings.HeadHtml))
        {
            head.Append(settings.HeadHtml);
        }

        var nav = settings.RenderNav ? RenderNav(manifest.Nav) : string.Empty;
        var bodyEnd = settings.BodyEndHtml ?? string.Empty;
        var lang = string.IsNullOrWhiteSpace(settings.Lang) ? "en" : settings.Lang;

        // The page's layout, resolved to markup. Regions are emitted verbatim in
        // the same place the builder's canvas draws them, which is the whole
        // point of them being files rather than a rendering rule: what the author
        // sees around their page is what ships around it.
        var (beforeRegions, afterRegions) = manifest.RegionsFor(page);
        var before = string.Concat(beforeRegions.Select(r => RegionMarkup(files, r)));
        var after = string.Concat(afterRegions.Select(r => RegionMarkup(files, r)));

        // A JSON script block, not JavaScript: the content is data and is parsed
        // as data, so a template containing "</script>" cannot end the element
        // early — the one escape that matters here.
        var components = registry.Length == 0
            ? string.Empty
            : $"<script type=\"application/json\" id=\"{ComponentRegistryId}\">{registry.Replace("<", "\\u003c")}</script>";
        var routeTable =
            $"<script type=\"application/json\" id=\"{RouteTableId}\">{routes.Replace("<", "\\u003c")}</script>";

        // The consent policy travels with the page rather than being fetched: the
        // runtime must know the answer *before* it may write a session id or fire the
        // first beacon, and a request to find out would either delay every pageview
        // or race it.
        var consentJson = JsonSerializer.Serialize(
            new
            {
                mode = settings.CookieConsent.Mode,
                settings.CookieConsent.Message,
                settings.CookieConsent.AcceptLabel,
                settings.CookieConsent.DeclineLabel,
                settings.CookieConsent.PolicyUrl,
                settings.CookieConsent.PolicyLabel,
                analytics = analyticsEnabled,
            },
            ConsentJson);
        var consent =
            $"<script type=\"application/json\" id=\"{ConsentConfigId}\">{consentJson.Replace("<", "\\u003c")}</script>";

        return $"""
            <!doctype html>
            <html lang="{Attr(lang)}">
            <head>
            <meta charset="utf-8" />
            <meta name="viewport" content="width=device-width, initial-scale=1" />
            {head}
            </head>
            <body>{nav}{before}{body}{after}{bodyEnd}{components}{routeTable}{consent}<script src="{HydrateScript}" defer></script></body>
            </html>
            """;
    }

    /// <summary>
    /// One region's markup, wrapped so its own CSS has something to target and so
    /// the builder can find it again in a published page.
    /// </summary>
    private static string RegionMarkup(IReadOnlyDictionary<string, string> files, RegionEntry region)
    {
        var markup = files.GetValueOrDefault(RegionHtmlPath(region.Slug), string.Empty);
        if (string.IsNullOrWhiteSpace(markup))
        {
            return string.Empty;
        }
        return $"<div class=\"dcms-region dcms-region-{Attr(region.Slug)}\" data-dcms-region=\"{Attr(region.Id)}\">{markup}</div>";
    }

    private static string RenderNav(IReadOnlyList<NavItem> nav)
    {
        if (nav.Count == 0)
        {
            return string.Empty;
        }
        var sb = new StringBuilder("<nav class=\"dcms-nav\">");
        AppendNavItems(nav, sb);
        sb.Append("</nav>");
        return sb.ToString();
    }

    private static void AppendNavItems(IReadOnlyList<NavItem> items, StringBuilder sb)
    {
        sb.Append("<ul>");
        foreach (var item in items)
        {
            sb.Append(CultureInfo.InvariantCulture,
                $"<li><a href=\"{Attr(item.Path)}\">{Text(item.Label)}</a>");
            if (item.Children is { Count: > 0 } children)
            {
                AppendNavItems(children, sb);
            }
            sb.Append("</li>");
        }
        sb.Append("</ul>");
    }

    private static string Text(string value) => WebUtility.HtmlEncode(value);

    private static string Attr(string value) => WebUtility.HtmlEncode(value);
}
