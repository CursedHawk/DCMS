using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace Dcms.Shared.Media;

/// <summary>
/// Sanitizes SVG uploads. SVG is XML that can carry scripts, event handlers and
/// external references, so a raw upload served inline would be a stored-XSS
/// vector. This parses the document with DTD/entity resolution disabled (no XXE)
/// and strips the active-content vectors — script/foreignObject-style elements,
/// <c>on*</c> event handlers and <c>javascript:</c>/non-image <c>data:</c> URIs —
/// before re-serialising. Raster images go through <see cref="MediaSanitizer"/>
/// instead; this is the SVG-only path.
/// </summary>
public static class SvgSanitizer
{
    // Elements that can execute script or embed foreign/active content. Matched by
    // local name so an explicit or defaulted namespace prefix doesn't smuggle them.
    private static readonly HashSet<string> ForbiddenElements = new(StringComparer.OrdinalIgnoreCase)
    {
        "script", "foreignObject", "iframe", "embed", "object", "handler", "audio", "video",
    };

    // Attributes that carry a URL and could point at an active-content scheme.
    private static readonly HashSet<string> UrlAttributes = new(StringComparer.OrdinalIgnoreCase)
    {
        "href", "src",
    };

    public static byte[] Sanitize(byte[] input)
    {
        XDocument doc;
        try
        {
            using var stream = new MemoryStream(input);
            using var reader = XmlReader.Create(stream, new XmlReaderSettings
            {
                // Ignore (not Prohibit) tolerates a harmless <!DOCTYPE svg> while
                // still never resolving external/parameter entities — no XXE.
                DtdProcessing = DtdProcessing.Ignore,
                XmlResolver = null,
                CloseInput = false,
            });
            doc = XDocument.Load(reader);
        }
        catch (Exception ex) when (ex is XmlException or InvalidOperationException)
        {
            throw new MediaSanitizationException("Invalid or unsafe SVG: " + ex.Message);
        }

        if (doc.Root is null || !string.Equals(doc.Root.Name.LocalName, "svg", StringComparison.OrdinalIgnoreCase))
        {
            throw new MediaSanitizationException("Not an SVG document.");
        }

        Clean(doc.Root);

        using var output = new MemoryStream();
        using (var writer = XmlWriter.Create(output, new XmlWriterSettings
        {
            OmitXmlDeclaration = false,
            Encoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
        }))
        {
            doc.Save(writer);
        }
        return output.ToArray();
    }

    private static void Clean(XElement element)
    {
        // Snapshot: children/attributes are mutated while iterating.
        foreach (var child in element.Elements().ToList())
        {
            if (ForbiddenElements.Contains(child.Name.LocalName))
            {
                child.Remove();
                continue;
            }
            Clean(child);
        }

        foreach (var attr in element.Attributes().ToList())
        {
            var local = attr.Name.LocalName;
            // Event handlers: onload, onclick, onbegin, onmouseover, …
            if (local.StartsWith("on", StringComparison.OrdinalIgnoreCase))
            {
                attr.Remove();
                continue;
            }
            if (UrlAttributes.Contains(local) && IsDangerousUrl(attr.Value))
            {
                attr.Remove();
            }
        }
    }

    /// <summary>
    /// True for URL schemes that can run code or load an active document.
    /// <c>javascript:</c>/<c>vbscript:</c> always; <c>data:</c> unless it is an
    /// image payload. Tolerates the classic obfuscations (leading whitespace, NUL
    /// bytes, and whitespace inside the scheme like "java\tscript:").
    /// </summary>
    private static bool IsDangerousUrl(string value)
    {
        var normalized = value.Replace("\0", string.Empty).TrimStart().ToLowerInvariant();
        var colon = normalized.IndexOf(':');
        if (colon < 0)
        {
            return false;
        }

        var scheme = new string(normalized[..colon].Where(c => !char.IsWhiteSpace(c)).ToArray());
        return scheme switch
        {
            "javascript" or "vbscript" => true,
            "data" => !normalized.StartsWith("data:image/", StringComparison.Ordinal),
            _ => false,
        };
    }
}
