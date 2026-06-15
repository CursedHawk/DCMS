using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats;

namespace Dcms.Shared.Media;

public sealed record SanitizedImage(byte[] Data, string ContentType, int Width, int Height);

public sealed class MediaSanitizationException(string message) : Exception(message);

/// <summary>
/// Re-encodes images through ImageSharp to neutralize embedded payloads and
/// strip metadata (EXIF/IPTC/XMP), enforcing dimension limits. SVG and other
/// non-raster image types are rejected (no safe re-encode in MVP).
/// </summary>
public sealed class MediaSanitizer
{
    public int MaxDimension { get; init; } = 8000;

    public SanitizedImage SanitizeImage(byte[] input)
    {
        IImageFormat format;
        try
        {
            format = Image.DetectFormat(input);
        }
        catch (Exception ex)
        {
            throw new MediaSanitizationException("Unrecognized or corrupt image: " + ex.Message);
        }

        using var image = Image.Load(input);

        if (image.Width > MaxDimension || image.Height > MaxDimension)
        {
            throw new MediaSanitizationException(
                $"Image exceeds the maximum dimension of {MaxDimension}px ({image.Width}x{image.Height}).");
        }

        // Strip all metadata profiles, then re-encode to the detected format.
        image.Metadata.ExifProfile = null;
        image.Metadata.IptcProfile = null;
        image.Metadata.XmpProfile = null;

        using var output = new MemoryStream();
        image.Save(output, format);

        return new SanitizedImage(output.ToArray(), format.DefaultMimeType, image.Width, image.Height);
    }
}
