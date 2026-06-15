using Dcms.Shared.Contracts.Events;

namespace Dcms.Shared.Media;

public sealed record SniffResult(string ContentType, MediaCategory Category);

/// <summary>
/// Detects a file's real content type from its leading bytes (magic numbers),
/// independent of the declared content type or extension. Uploads whose declared
/// type disagrees with the sniffed type are rejected by the upload pipeline.
/// </summary>
public static class ContentSniffer
{
    public static SniffResult? Sniff(ReadOnlySpan<byte> header)
    {
        if (StartsWith(header, [0xFF, 0xD8, 0xFF]))
        {
            return new SniffResult("image/jpeg", MediaCategory.Image);
        }
        if (StartsWith(header, [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]))
        {
            return new SniffResult("image/png", MediaCategory.Image);
        }
        if (StartsWith(header, "GIF87a"u8) || StartsWith(header, "GIF89a"u8))
        {
            return new SniffResult("image/gif", MediaCategory.Image);
        }
        if (header.Length >= 12 && StartsWith(header, "RIFF"u8) && Equals(header.Slice(8, 4), "WEBP"u8))
        {
            return new SniffResult("image/webp", MediaCategory.Image);
        }
        if (header.Length >= 12 && StartsWith(header, "RIFF"u8) && Equals(header.Slice(8, 4), "WAVE"u8))
        {
            return new SniffResult("audio/wav", MediaCategory.Audio);
        }
        if (header.Length >= 12 && Equals(header.Slice(4, 4), "ftyp"u8))
        {
            return new SniffResult("video/mp4", MediaCategory.Video);
        }
        if (StartsWith(header, "OggS"u8))
        {
            return new SniffResult("audio/ogg", MediaCategory.Audio);
        }
        if (StartsWith(header, "ID3"u8) || StartsWith(header, [0xFF, 0xFB]) || StartsWith(header, [0xFF, 0xF3]))
        {
            return new SniffResult("audio/mpeg", MediaCategory.Audio);
        }
        if (StartsWith(header, "%PDF-"u8))
        {
            return new SniffResult("application/pdf", MediaCategory.File);
        }
        if (StartsWith(header, [0x50, 0x4B, 0x03, 0x04]))
        {
            return new SniffResult("application/zip", MediaCategory.File);
        }
        return null;
    }

    private static bool StartsWith(ReadOnlySpan<byte> data, ReadOnlySpan<byte> prefix)
        => data.Length >= prefix.Length && data[..prefix.Length].SequenceEqual(prefix);

    private static bool Equals(ReadOnlySpan<byte> a, ReadOnlySpan<byte> b) => a.SequenceEqual(b);
}
