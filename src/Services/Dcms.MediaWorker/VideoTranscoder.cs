using System.Globalization;
using System.Text;
using FFMpegCore;
using FFMpegCore.Enums;

namespace Dcms.MediaWorker;

public sealed record HlsFile(string Name, string LocalPath, string ContentType);

public sealed record VideoTranscodeResult(
    IReadOnlyList<HlsFile> HlsFiles,    // everything under the hls/ prefix (playlists + segments)
    IReadOnlyList<int> Renditions,      // heights produced (for variant records)
    string PosterPath);

/// <summary>
/// Produces an HLS ladder (1080/720/480, skipping heights above the source) with
/// 6-second H.264/AAC segments, a hand-written master playlist, and a poster
/// frame. Each rendition is a separate ffmpeg pass for predictable output.
/// </summary>
public sealed class VideoTranscoder
{
    private static readonly (int Height, int Bandwidth)[] Ladder =
    [
        (1080, 5_000_000),
        (720, 2_800_000),
        (480, 1_400_000),
    ];

    public async Task<VideoTranscodeResult> TranscodeAsync(string inputFile, string workDir, CancellationToken ct)
    {
        var hlsDir = Path.Combine(workDir, "hls");
        Directory.CreateDirectory(hlsDir);

        var probe = await FFProbe.AnalyseAsync(inputFile, cancellationToken: ct);
        var sourceHeight = probe.PrimaryVideoStream?.Height ?? 1080;

        var produced = new List<int>();
        foreach (var (height, _) in Ladder)
        {
            if (height > sourceHeight && produced.Count > 0)
            {
                continue; // never upscale, but always produce at least one rendition
            }
            var target = Math.Min(height, sourceHeight);
            await TranscodeRenditionAsync(inputFile, hlsDir, height, target, ct);
            produced.Add(height);
        }

        WriteMasterPlaylist(hlsDir, produced);

        var poster = Path.Combine(workDir, "poster.jpg");
        await FFMpeg.SnapshotAsync(inputFile, poster, captureTime: TimeSpan.FromSeconds(1));

        var files = Directory.GetFiles(hlsDir)
            .Select(p => new HlsFile(Path.GetFileName(p), p, ContentTypeFor(p)))
            .ToList();

        return new VideoTranscodeResult(files, produced, poster);
    }

    private static async Task TranscodeRenditionAsync(string input, string hlsDir, int label, int targetHeight, CancellationToken ct)
    {
        var playlist = Path.Combine(hlsDir, $"r{label}.m3u8");
        var segmentPattern = Path.Combine(hlsDir, $"r{label}_%03d.ts");

        await FFMpegArguments
            .FromFileInput(input)
            .OutputToFile(playlist, overwrite: true, options => options
                .WithVideoCodec("libx264")
                .WithConstantRateFactor(21)
                .WithAudioCodec("aac")
                .WithAudioBitrate(128)
                .WithCustomArgument($"-vf scale=-2:{targetHeight}")
                .WithCustomArgument("-hls_time 6")
                .WithCustomArgument("-hls_playlist_type vod")
                .WithCustomArgument($"-hls_segment_filename \"{segmentPattern}\"")
                .ForceFormat("hls"))
            .CancellableThrough(ct)
            .ProcessAsynchronously();
    }

    private static void WriteMasterPlaylist(string hlsDir, IReadOnlyList<int> heights)
    {
        var sb = new StringBuilder();
        sb.AppendLine("#EXTM3U");
        sb.AppendLine("#EXT-X-VERSION:3");
        foreach (var height in heights)
        {
            var bandwidth = Array.Find(Ladder, l => l.Height == height).Bandwidth;
            var width = height * 16 / 9;
            sb.AppendLine(string.Create(CultureInfo.InvariantCulture,
                $"#EXT-X-STREAM-INF:BANDWIDTH={bandwidth},RESOLUTION={width}x{height}"));
            sb.AppendLine($"r{height}.m3u8");
        }
        File.WriteAllText(Path.Combine(hlsDir, "master.m3u8"), sb.ToString());
    }

    private static string ContentTypeFor(string path) => Path.GetExtension(path) switch
    {
        ".m3u8" => "application/vnd.apple.mpegurl",
        ".ts" => "video/mp2t",
        _ => "application/octet-stream",
    };
}
