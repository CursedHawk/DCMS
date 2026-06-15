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
}
