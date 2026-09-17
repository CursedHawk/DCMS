using Ganss.Xss;

namespace Dcms.Shared.Security;

/// <summary>
/// Server-side allow-list sanitizer for author-supplied CMS rich text (SEC-13).
///
/// <para>Rich-text content fields are HTML authored in TipTap and, until this, were persisted
/// verbatim and injected into the published site with <c>innerHTML</c> (the site template's
/// <c>RichText</c> component and the Mode A hydrate runtime). TipTap's schema drops
/// <c>&lt;script&gt;</c> on parse in the admin origin, but <c>content:write</c> is a granular,
/// delegable permission and nothing stopped a low-tier editor from POSTing hand-crafted HTML
/// straight to the API — stored XSS on the tenant's public site. This closes that on write.</para>
///
/// <para>The allow-list is intentionally narrow: the formatting a TipTap document can actually
/// produce (headings, emphasis, lists, quotes, tables, links, images, code) and nothing that
/// carries script. <c>on*</c> handlers, <c>&lt;script&gt;</c>/<c>&lt;style&gt;</c>, form and
/// object/embed elements, and any non-http(s)/mailto/tel/data scheme are removed. A vetted
/// parser (AngleSharp, via mganss/HtmlSanitizer) is used rather than a regex or a hand-rolled
/// element walker because HTML's parsing quirks — mutation XSS, attribute smuggling, malformed
/// nesting a browser silently "corrects" into an executable shape — are exactly what defeats
/// hand-rolled strippers. The <see cref="Dcms.Shared"/> SVG path is XML and stays hand-written;
/// HTML is not, and must not be treated as if it were.</para>
/// </summary>
public static class HtmlContentSanitizer
{
    // One configured instance, reused. HtmlSanitizer is thread-safe for Sanitize once its
    // allow-lists are populated, and building the AngleSharp configuration per call would be
    // pure waste on the content write path.
    private static readonly HtmlSanitizer Sanitizer = Build();

    private static HtmlSanitizer Build()
    {
        var s = new HtmlSanitizer(new HtmlSanitizerOptions
        {
            AllowedTags = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "p", "br", "hr", "span", "div",
                "h1", "h2", "h3", "h4", "h5", "h6",
                "strong", "b", "em", "i", "u", "s", "strike", "del", "ins", "mark", "sub", "sup", "small",
                "blockquote", "pre", "code",
                "ul", "ol", "li",
                "a", "img", "figure", "figcaption",
                "table", "thead", "tbody", "tfoot", "tr", "th", "td", "caption", "colgroup", "col",
            },
            AllowedAttributes = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "href", "title", "target", "rel",
                "src", "alt", "width", "height",
                "colspan", "rowspan",
                "class", "id", "dir",
                "start", "type", "reversed",
                // Data attributes the editor/hydrate use for binding markers stay allowed
                // wholesale below via AllowDataAttributes; nothing here needs to name them.
            },
            AllowedCssProperties = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "text-align", "color", "background-color", "font-weight", "font-style",
                "text-decoration", "width", "height",
            },
            // The only schemes a link or image may resolve to. This MUST be set on the options
            // the constructor reads — mutating HtmlSanitizer.AllowedSchemes after construction
            // does not take effect against the options-provided set. Everything else —
            // javascript:, vbscript:, and data: in particular — is dropped with the attribute,
            // so no src/href can carry script. data: is excluded outright rather than gated to
            // images: inline images belong in the media library (a MediaRef), and a permitted
            // data: URL is a standing bypass surface (data:image/svg+xml carries script,
            // data:text/html is a page). There is deliberately no FilterUrl override — that
            // event's SanitizedUrl defaults to the ORIGINAL url, so a handler that only
            // conditionally clears it silently re-permits every scheme.
            AllowedSchemes = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "http", "https", "mailto", "tel",
            },
            // The attributes whose value is a URL and must be scheme-checked. Naming them
            // explicitly is what makes the AllowedSchemes list bite on href/src.
            UriAttributes = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "href", "src",
            },
        });

        s.AllowDataAttributes = true;

        return s;
    }

    /// <summary>
    /// Returns <paramref name="html"/> with everything outside the allow-list removed. Null or
    /// whitespace passes through unchanged (there is nothing to sanitize and callers store it
    /// as-is). The result is safe to persist and to inject with <c>innerHTML</c> on the
    /// published site.
    /// </summary>
    public static string? Sanitize(string? html)
    {
        if (string.IsNullOrWhiteSpace(html)) return html;
        return Sanitizer.Sanitize(html);
    }
}
