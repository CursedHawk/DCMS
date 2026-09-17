using Dcms.Shared.Security;

namespace Dcms.UnitTests.Security;

/// <summary>
/// SEC-13: rich-text content is author-supplied HTML injected into the published site with
/// innerHTML. These pin the allow-list — the formatting a TipTap document produces survives,
/// and every script-carrying vector is stripped — so a delegated <c>content:write</c> editor
/// cannot plant stored XSS on the tenant's public site.
/// </summary>
public class HtmlContentSanitizerTests
{
    [Fact]
    public void Keeps_ordinary_formatting()
    {
        var html = "<h2>Title</h2><p>Hello <strong>world</strong> and <em>friends</em>.</p>"
                   + "<ul><li>one</li><li>two</li></ul>"
                   + "<blockquote>quote</blockquote><pre><code>code()</code></pre>";
        var clean = HtmlContentSanitizer.Sanitize(html)!;

        clean.Should().Contain("<h2>").And.Contain("<strong>").And.Contain("<em>");
        clean.Should().Contain("<li>one</li>").And.Contain("<blockquote>").And.Contain("<code>");
    }

    [Fact]
    public void Strips_script_elements()
    {
        var clean = HtmlContentSanitizer.Sanitize("<p>hi</p><script>alert(1)</script>")!;
        clean.Should().Contain("<p>hi</p>");
        clean.Should().NotContain("script").And.NotContain("alert");
    }

    [Fact]
    public void Strips_inline_event_handlers()
    {
        var clean = HtmlContentSanitizer.Sanitize("<p onclick=\"steal()\">click</p>")!;
        clean.Should().Contain("click");
        clean.Should().NotContain("onclick").And.NotContain("steal");
    }

    [Fact]
    public void Drops_javascript_scheme_links()
    {
        var clean = HtmlContentSanitizer.Sanitize("<a href=\"javascript:alert(1)\">x</a>")!;
        clean.Should().NotContain("javascript:").And.NotContain("alert");
    }

    [Fact]
    public void Keeps_http_and_mailto_links()
    {
        var clean = HtmlContentSanitizer.Sanitize(
            "<a href=\"https://example.com\">site</a> <a href=\"mailto:a@b.co\">mail</a>")!;
        clean.Should().Contain("https://example.com").And.Contain("mailto:a@b.co");
    }

    [Fact]
    public void Drops_all_data_urls()
    {
        // data: is not an allowed scheme: data:text/html is a page, data:image/svg+xml carries
        // script, and inline images belong in the media library. All are dropped with the src.
        var htmlData = HtmlContentSanitizer.Sanitize("<img src=\"data:text/html,<script>alert(1)</script>\">")!;
        htmlData.Should().NotContain("text/html").And.NotContain("alert");

        var pngData = HtmlContentSanitizer.Sanitize("<img src=\"data:image/png;base64,iVBORw0KGgo=\" alt=\"x\">")!;
        pngData.Should().NotContain("data:");
    }

    [Fact]
    public void Keeps_http_image_sources()
    {
        var clean = HtmlContentSanitizer.Sanitize("<img src=\"https://cdn.example.com/a.png\" alt=\"x\">")!;
        clean.Should().Contain("https://cdn.example.com/a.png");
    }

    [Fact]
    public void Removes_object_and_iframe_and_style()
    {
        var clean = HtmlContentSanitizer.Sanitize(
            "<p>ok</p><iframe src=\"//evil\"></iframe><object data=\"x\"></object><style>body{}</style>")!;
        clean.Should().Contain("<p>ok</p>");
        clean.Should().NotContain("iframe").And.NotContain("object").And.NotContain("<style");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Passes_through_empty_input(string? input)
    {
        HtmlContentSanitizer.Sanitize(input).Should().Be(input);
    }
}
