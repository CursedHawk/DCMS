using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using Dcms.Shared.Data.Sites;

namespace Dcms.SiteBuilder;

public sealed record RenderedPage(string FileName, string Html);

/// <summary>
/// Mode A prerenderer: walks the component tree to static HTML, one file per
/// page. Layout primitives render directly; plugin/data-bound components emit a
/// placeholder element carrying their bindings for client-side hydration.
///
/// Pages with a <see cref="SitePage.Canvas"/> use the free-canvas (Wix-style)
/// model: the page body is a positioned stage and every node carries an absolute
/// <see cref="NodeLayout"/> (left/top/width/height/z-index) emitted as inline
/// styles, with per-breakpoint overrides collected into a page stylesheet. Pages
/// without a canvas keep the original flow layout (back-compat with AI/legacy
/// definitions that omit layout). No Node dependency.
/// </summary>
public sealed class SiteRenderer
{
    // Editor breakpoint keys → max-width media queries.
    private static readonly (string Key, int MaxWidth)[] Breakpoints =
    [
        ("tablet", 1024),
        ("mobile", 640),
    ];

    public IReadOnlyList<RenderedPage> Render(SiteDefinition definition)
    {
        var pages = new List<RenderedPage>();
        foreach (var page in definition.Pages)
        {
            pages.Add(new RenderedPage(FileNameFor(page.Path), RenderPage(definition, page)));
        }

        if (pages.Count == 0)
        {
            pages.Add(new RenderedPage("index.html", EmptyShell(definition)));
        }
        return pages;
    }

    public static string FileNameFor(string path)
    {
        var trimmed = path.Trim('/');
        return string.IsNullOrEmpty(trimmed) ? "index.html" : $"{trimmed.Replace('/', '_')}.html";
    }

    private string RenderPage(SiteDefinition definition, SitePage page)
    {
        var body = new StringBuilder();
        var responsiveCss = new StringBuilder();
        RenderNav(definition, body);

        if (page.Canvas is { } canvas)
        {
            // Free-canvas: positioned stage; render the root's children as absolute
            // siblings anchored to the stage.
            body.Append(CultureInfo.InvariantCulture,
                $"<div class=\"dcms-canvas\" style=\"position:relative;width:{Num(canvas.Width)}px;min-height:{Num(canvas.MinHeight)}px;margin:0 auto;\">");
            if (page.Root is not null)
            {
                foreach (var child in page.Root.Children)
                {
                    RenderNode(child, body, responsiveCss, absolute: true);
                }
            }
            body.Append("</div>");
        }
        else if (page.Root is not null)
        {
            RenderNode(page.Root, body, responsiveCss, absolute: false);
        }

        return Document(page.Seo, definition.Theme, body.ToString(), responsiveCss.ToString());
    }

    private void RenderNav(SiteDefinition definition, StringBuilder sb)
    {
        if (definition.Nav.Count == 0)
        {
            return;
        }
        sb.Append("<nav class=\"dcms-nav\">");
        foreach (var item in definition.Nav)
        {
            sb.Append(CultureInfo.InvariantCulture, $"<a href=\"{Attr(item.Path)}\">{Text(item.Label)}</a>");
        }
        sb.Append("</nav>");
    }

    private void RenderNode(ComponentNode node, StringBuilder sb, StringBuilder css, bool absolute)
    {
        // Absolute mode: wrap the node in a positioned box and collect its
        // responsive overrides. The wrapper is itself a containing block, so any
        // absolutely positioned children anchor to it.
        if (absolute && node.Layout is { } layout)
        {
            var cls = "n-" + Sanitize(node.Id);
            var z = layout.Z is { } zi ? $"z-index:{zi.ToString(CultureInfo.InvariantCulture)};" : string.Empty;
            sb.Append(CultureInfo.InvariantCulture,
                $"<div class=\"dcms-node {cls}\" style=\"position:absolute;left:{Num(layout.X)}px;top:{Num(layout.Y)}px;width:{Num(layout.W)}px;height:{Num(layout.H)}px;{z}\">");
            CollectBreakpointCss(css, cls, layout);
            RenderInner(node, sb, css, absolute: true);
            sb.Append("</div>");
            return;
        }

        RenderInner(node, sb, css, absolute);
    }

    private void RenderInner(ComponentNode node, StringBuilder sb, StringBuilder css, bool absolute)
    {
        // Data-bound or plugin components render as hydration placeholders carrying
        // their type, props (e.g. heading) and bindings for /_dcms/hydrate.js.
        if (node.Bindings.Count > 0)
        {
            sb.Append(CultureInfo.InvariantCulture,
                $"<div data-dcms-component=\"{Attr(node.Type)}\" data-dcms-props=\"{Attr(SerializeProps(node))}\" data-dcms-bindings=\"{Attr(SerializeBindings(node))}\"></div>");
            return;
        }

        switch (node.Type)
        {
            case "Section" or "Container" or "Stack" or "Grid":
                sb.Append(CultureInfo.InvariantCulture, $"<section class=\"dcms-{node.Type.ToLowerInvariant()}\">");
                RenderChildren(node, sb, css, absolute);
                sb.Append("</section>");
                break;
            case "Text" or "Heading":
                var tag = Prop(node, "variant") ?? (node.Type == "Heading" ? "h2" : "p");
                if (tag is not ("h1" or "h2" or "h3" or "h4" or "p"))
                {
                    tag = "p";
                }
                sb.Append(CultureInfo.InvariantCulture, $"<{tag}>{Text(Prop(node, "text") ?? string.Empty)}</{tag}>");
                break;
            case "Image":
                sb.Append(CultureInfo.InvariantCulture,
                    $"<img src=\"{Attr(MediaUrl(Prop(node, "src") ?? string.Empty))}\" alt=\"{Attr(Prop(node, "alt") ?? string.Empty)}\" />");
                break;
            case "Button":
                sb.Append(CultureInfo.InvariantCulture,
                    $"<a class=\"dcms-button\" href=\"{Attr(Prop(node, "href") ?? "#")}\">{Text(Prop(node, "label") ?? "Button")}</a>");
                break;
            case "Hero":
                sb.Append("<section class=\"dcms-hero\">");
                sb.Append(CultureInfo.InvariantCulture, $"<h1>{Text(Prop(node, "title") ?? string.Empty)}</h1>");
                if (Prop(node, "subtitle") is { } sub)
                {
                    sb.Append(CultureInfo.InvariantCulture, $"<p>{Text(sub)}</p>");
                }
                RenderChildren(node, sb, css, absolute);
                sb.Append("</section>");
                break;
            default:
                sb.Append(CultureInfo.InvariantCulture, $"<div data-component=\"{Attr(node.Type)}\">");
                RenderChildren(node, sb, css, absolute);
                sb.Append("</div>");
                break;
        }
    }

    private void RenderChildren(ComponentNode node, StringBuilder sb, StringBuilder css, bool absolute)
    {
        foreach (var child in node.Children)
        {
            RenderNode(child, sb, css, absolute);
        }
    }

    private static void CollectBreakpointCss(StringBuilder css, string cls, NodeLayout layout)
    {
        if (layout.Breakpoints is null)
        {
            return;
        }
        foreach (var (key, maxWidth) in Breakpoints)
        {
            if (!layout.Breakpoints.TryGetValue(key, out var bp) || bp is null)
            {
                continue;
            }
            var rule = new StringBuilder();
            if (bp.X is { } x) rule.Append(CultureInfo.InvariantCulture, $"left:{Num(x)}px;");
            if (bp.Y is { } y) rule.Append(CultureInfo.InvariantCulture, $"top:{Num(y)}px;");
            if (bp.W is { } w) rule.Append(CultureInfo.InvariantCulture, $"width:{Num(w)}px;");
            if (bp.H is { } h) rule.Append(CultureInfo.InvariantCulture, $"height:{Num(h)}px;");
            if (bp.Z is { } z) rule.Append(CultureInfo.InvariantCulture, $"z-index:{z.ToString(CultureInfo.InvariantCulture)};");
            if (rule.Length > 0)
            {
                css.Append(CultureInfo.InvariantCulture, $"@media (max-width:{maxWidth}px){{.{cls}{{{rule}}}}}");
            }
        }
    }

    private static string Document(SeoMeta seo, ThemeTokens theme, string body, string responsiveCss)
    {
        var css = new StringBuilder(":root{");
        foreach (var (k, v) in theme.Colors)
        {
            css.Append(CultureInfo.InvariantCulture, $"--color-{Css(k)}:{Css(v)};");
        }
        foreach (var (k, v) in theme.Fonts)
        {
            css.Append(CultureInfo.InvariantCulture, $"--font-{Css(k)}:{Css(v)};");
        }
        if (theme.Radius is { } radius)
        {
            css.Append(CultureInfo.InvariantCulture, $"--radius:{Css(radius)};");
        }
        css.Append('}');
        css.Append("*{box-sizing:border-box;}.dcms-node{overflow:hidden;}");
        css.Append(responsiveCss);

        var description = seo.Description is null ? string.Empty : $"<meta name=\"description\" content=\"{Attr(seo.Description)}\" />";
        return $"""
            <!doctype html>
            <html lang="en">
            <head>
            <meta charset="utf-8" />
            <meta name="viewport" content="width=device-width, initial-scale=1" />
            <title>{Text(seo.Title)}</title>
            {description}
            <style>{css}</style>
            </head>
            <body>{body}<script src="/_dcms/hydrate.js" defer></script></body>
            </html>
            """;
    }

    private static string EmptyShell(SiteDefinition definition)
        => Document(new SeoMeta { Title = "Untitled site" }, definition.Theme, "<main></main>", string.Empty);

    private static string? Prop(ComponentNode node, string name)
        => node.Props.TryGetValue(name, out var v) ? v.AsString() : null;

    // A bare media asset id maps to the tenant delivery endpoint; anything else
    // (an absolute/relative URL) is left untouched. Mirrors hydrate.js's mediaUrl.
    private static string MediaUrl(string reference)
        => MediaIdPattern.IsMatch(reference) ? $"/api/media/{reference}/original" : reference;

    private static readonly System.Text.RegularExpressions.Regex MediaIdPattern =
        new("^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Compiled);

    // Serialize bindings with their query intact — hydrate.js needs the
    // contentType (held in the query) to build the delivery URL.
    private static string SerializeBindings(ComponentNode node)
        => JsonSerializer.Serialize(node.Bindings.Select(b => new
        {
            propPath = b.PropPath,
            instanceSlug = b.Source.InstanceSlug,
            query = b.Source.Query,
        }));

    private static string SerializeProps(ComponentNode node)
        => JsonSerializer.Serialize(node.Props);

    private static string Num(double value) => value.ToString("0.##", CultureInfo.InvariantCulture);

    private static string Sanitize(string id)
    {
        var sb = new StringBuilder(id.Length);
        foreach (var c in id)
        {
            sb.Append(char.IsAsciiLetterOrDigit(c) || c is '-' or '_' ? c : '-');
        }
        return sb.Length == 0 ? "x" : sb.ToString();
    }

    private static string Text(string value) => WebUtility.HtmlEncode(value);
    private static string Attr(string value) => WebUtility.HtmlEncode(value);
    private static string Css(string value) => value.Replace("<", string.Empty).Replace(">", string.Empty).Replace(";", string.Empty);
}
