using System.Text;
using Dcms.Shared.Contracts.Events;
using Dcms.Shared.Media;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Png;
using SixLabors.ImageSharp.Metadata.Profiles.Exif;
using SixLabors.ImageSharp.PixelFormats;

namespace Dcms.UnitTests.Media;

public class MediaProcessingTests
{
    private static byte[] CreatePng(int width, int height, bool withExif = false)
    {
        using var image = new Image<Rgba32>(width, height, new Rgba32(10, 120, 220));
        if (withExif)
        {
            image.Metadata.ExifProfile = new ExifProfile();
            image.Metadata.ExifProfile.SetValue(ExifTag.Copyright, "secret");
        }
        using var ms = new MemoryStream();
        image.Save(ms, new PngEncoder());
        return ms.ToArray();
    }

    [Fact]
    public void Sanitizer_strips_metadata_and_preserves_dimensions()
    {
        var input = CreatePng(120, 90, withExif: true);

        var result = new MediaSanitizer().SanitizeImage(input);

        result.Width.Should().Be(120);
        result.Height.Should().Be(90);
        result.ContentType.Should().Be("image/png");

        using var reloaded = Image.Load(result.Data);
        reloaded.Metadata.ExifProfile.Should().BeNull();
    }

    [Fact]
    public void Sanitizer_rejects_images_over_the_dimension_limit()
    {
        var input = CreatePng(300, 300);
        var sanitizer = new MediaSanitizer { MaxDimension = 100 };

        var act = () => sanitizer.SanitizeImage(input);
        act.Should().Throw<MediaSanitizationException>();
    }

    [Fact]
    public void Sanitizer_rejects_non_image_bytes()
    {
        var act = () => new MediaSanitizer().SanitizeImage("not an image"u8.ToArray());
        act.Should().Throw<MediaSanitizationException>();
    }

    [Fact]
    public void Webp_ladder_produces_renditions_up_to_source_width_plus_thumb()
    {
        var input = CreatePng(1000, 800);

        var variants = new WebpLadderGenerator().Generate(input);

        var kinds = variants.Select(v => v.Kind).ToList();
        kinds.Should().Contain(["webp-320", "webp-640", "thumb"]);
        kinds.Should().NotContain(["webp-1280", "webp-1920"]); // wider than the 1000px source
        variants.Should().AllSatisfy(v =>
        {
            v.ContentType.Should().Be("image/webp");
            v.Data.Should().NotBeEmpty();
            v.Width.Should().BeLessThanOrEqualTo(1000);
        });
    }

    [Theory]
    [InlineData(MediaCategory.Image, "image/png")]
    public void Sniffer_detects_png(MediaCategory expectedCategory, string expectedType)
    {
        var png = CreatePng(10, 10);

        var result = ContentSniffer.Sniff(png);

        result.Should().NotBeNull();
        result!.Category.Should().Be(expectedCategory);
        result.ContentType.Should().Be(expectedType);
    }

    [Fact]
    public void Sniffer_returns_null_for_unknown_bytes()
        => ContentSniffer.Sniff([0x00, 0x01, 0x02, 0x03]).Should().BeNull();

    [Theory]
    [InlineData("<svg xmlns=\"http://www.w3.org/2000/svg\"><rect/></svg>")]
    [InlineData("<?xml version=\"1.0\"?>\n<!-- a logo -->\n<svg xmlns=\"http://www.w3.org/2000/svg\"></svg>")]
    [InlineData("﻿<svg xmlns=\"http://www.w3.org/2000/svg\"/>")]
    public void Sniffer_detects_svg(string markup)
    {
        var result = ContentSniffer.Sniff(Encoding.UTF8.GetBytes(markup));

        result.Should().NotBeNull();
        result!.Category.Should().Be(MediaCategory.Image);
        result.ContentType.Should().Be("image/svg+xml");
    }

    [Fact]
    public void Sniffer_does_not_treat_html_with_inline_svg_as_svg()
    {
        var html = "<!DOCTYPE html><html><body><svg></svg></body></html>"u8.ToArray();

        ContentSniffer.Sniff(html).Should().BeNull();
    }

    [Fact]
    public void SvgSanitizer_strips_scripts_handlers_and_javascript_uris_but_keeps_geometry()
    {
        var dirty = """
            <svg xmlns="http://www.w3.org/2000/svg" xmlns:xlink="http://www.w3.org/1999/xlink" width="24" height="24">
              <script>alert(1)</script>
              <rect width="24" height="24" onload="steal()" fill="#1d4ed8"/>
              <a xlink:href="javascript:alert(2)"><circle cx="12" cy="12" r="6"/></a>
            </svg>
            """u8.ToArray();

        var clean = Encoding.UTF8.GetString(SvgSanitizer.Sanitize(dirty));

        clean.Should().NotContain("<script");
        clean.Should().NotContain("onload");
        clean.Should().NotContain("javascript:");
        // Legitimate drawing survives.
        clean.Should().Contain("<rect");
        clean.Should().Contain("#1d4ed8");
        clean.Should().Contain("<circle");
    }

    [Fact]
    public void SvgSanitizer_tolerates_a_doctype_without_resolving_entities()
    {
        var withDoctype = """
            <?xml version="1.0"?>
            <!DOCTYPE svg PUBLIC "-//W3C//DTD SVG 1.1//EN" "http://www.w3.org/Graphics/SVG/1.1/DTD/svg11.dtd">
            <svg xmlns="http://www.w3.org/2000/svg"><rect width="4" height="4"/></svg>
            """u8.ToArray();

        var act = () => SvgSanitizer.Sanitize(withDoctype);

        act.Should().NotThrow();
    }

    [Fact]
    public void SvgSanitizer_rejects_non_svg_input()
    {
        var act = () => SvgSanitizer.Sanitize("not xml at all"u8.ToArray());

        act.Should().Throw<MediaSanitizationException>();
    }
}
