using System.Net;
using System.Text;
using System.Text.Json;
using Dcms.Shared.Data.Sites;

namespace Dcms.SiteBuilder;

public sealed record RenderedPage(string FileName, string Html);

/// <summary>
/// Mode A prerenderer: walks the component tree to static HTML, one file per
/// page. Layout primitives render directly; plugin/data-bound components emit a
/// placeholder element carrying their bindings for client-side hydration (the
/// hydration runtime is wired in a later phase). No Node dependency.
/// </summary>
public sealed class SiteRenderer
{
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
        RenderNav(definition, body);
        if (page.Root is not null)
        {
            RenderNode(page.Root, body);
        }
        return Document(page.Seo, definition.Theme, body.ToString());
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
            sb.Append($"<a href=\"{Attr(item.Path)}\">{Text(item.Label)}</a>");
        }
        sb.Append("</nav>");
    }

    private void RenderNode(ComponentNode node, StringBuilder sb)
    {
        // Data-bound or plugin components render as hydration placeholders.
        if (node.Bindings.Count > 0)
        {
            sb.Append($"<div data-dcms-component=\"{Attr(node.Type)}\" data-dcms-bindings=\"{Attr(SerializeBindings(node))}\"></div>");
            return;
        }

        switch (node.Type)
        {
            case "Section" or "Container" or "Stack" or "Grid":
                sb.Append($"<section class=\"dcms-{node.Type.ToLowerInvariant()}\">");
                RenderChildren(node, sb);
                sb.Append("</section>");
                break;
            case "Text" or "Heading":
                var tag = Prop(node, "variant") ?? (node.Type == "Heading" ? "h2" : "p");
                if (tag is not ("h1" or "h2" or "h3" or "h4" or "p"))
                {
                    tag = "p";
                }
                sb.Append($"<{tag}>{Text(Prop(node, "text") ?? string.Empty)}</{tag}>");
                break;
            case "Image":
                sb.Append($"<img src=\"{Attr(Prop(node, "src") ?? string.Empty)}\" alt=\"{Attr(Prop(node, "alt") ?? string.Empty)}\" />");
                break;
            case "Button":
                sb.Append($"<a class=\"dcms-button\" href=\"{Attr(Prop(node, "href") ?? "#")}\">{Text(Prop(node, "label") ?? "Button")}</a>");
                break;
            case "Hero":
                sb.Append("<section class=\"dcms-hero\">");
                sb.Append($"<h1>{Text(Prop(node, "title") ?? string.Empty)}</h1>");
                if (Prop(node, "subtitle") is { } sub)
                {
                    sb.Append($"<p>{Text(sub)}</p>");
                }
                RenderChildren(node, sb);
                sb.Append("</section>");
                break;
            default:
                sb.Append($"<div data-component=\"{Attr(node.Type)}\">");
                RenderChildren(node, sb);
                sb.Append("</div>");
                break;
        }
    }

    private void RenderChildren(ComponentNode node, StringBuilder sb)
    {
        foreach (var child in node.Children)
        {
            RenderNode(child, sb);
        }
    }

    private static string Document(SeoMeta seo, ThemeTokens theme, string body)
    {
        var css = new StringBuilder(":root{");
        foreach (var (k, v) in theme.Colors)
        {
            css.Append($"--color-{Css(k)}:{Css(v)};");
        }
        foreach (var (k, v) in theme.Fonts)
        {
            css.Append($"--font-{Css(k)}:{Css(v)};");
        }
        css.Append('}');

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
        => Document(new SeoMeta { Title = "Untitled site" }, definition.Theme, "<main></main>");

    private static string? Prop(ComponentNode node, string name)
        => node.Props.TryGetValue(name, out var v) ? v.AsString() : null;

    private static string SerializeBindings(ComponentNode node)
        => JsonSerializer.Serialize(node.Bindings.Select(b => new { b.PropPath, b.Source.InstanceSlug }));

    private static string Text(string value) => WebUtility.HtmlEncode(value);
    private static string Attr(string value) => WebUtility.HtmlEncode(value);
    private static string Css(string value) => value.Replace("<", string.Empty).Replace(">", string.Empty).Replace(";", string.Empty);
}
