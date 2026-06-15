using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Webp;
using SixLabors.ImageSharp.Processing;

namespace Dcms.Shared.Media;

public sealed record GeneratedVariant(string Kind, byte[] Data, string ContentType, int Width, int Height);

/// <summary>
/// Produces the webp responsive ladder for an image: widths 320/640/1280/1920
/// (skipping any wider than the source) plus a 160px square-ish thumbnail. All
/// outputs are webp.
/// </summary>
public sealed class WebpLadderGenerator
{
    private static readonly int[] Widths = [320, 640, 1280, 1920];
    private const int ThumbWidth = 160;

    public IReadOnlyList<GeneratedVariant> Generate(byte[] imageBytes)
    {
        using var source = Image.Load(imageBytes);
        var variants = new List<GeneratedVariant>();

        foreach (var width in Widths)
        {
            if (width > source.Width)
            {
                continue;
            }
            variants.Add(Encode(source, $"webp-{width}", width));
        }

        // Always include at least one rendition (the original width) and a thumb.
        if (variants.Count == 0)
        {
            variants.Add(Encode(source, $"webp-{source.Width}", source.Width));
        }
        variants.Add(Encode(source, "thumb", Math.Min(ThumbWidth, source.Width)));

        return variants;
    }

    private static GeneratedVariant Encode(Image source, string kind, int targetWidth)
    {
        using var clone = source.Clone(ctx => ctx.Resize(new ResizeOptions
        {
            Mode = ResizeMode.Max,
            Size = new Size(targetWidth, 0),
        }));

        using var output = new MemoryStream();
        clone.Save(output, new WebpEncoder { Quality = 80 });
        return new GeneratedVariant(kind, output.ToArray(), "image/webp", clone.Width, clone.Height);
    }
}
