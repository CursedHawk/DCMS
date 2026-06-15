extern alias MediaWorkerApp;
using FFMpegCore;

namespace Dcms.IntegrationTests.Media;

/// <summary>
/// Exercises the real ffmpeg transcoder. Skipped unless ffmpeg/ffprobe are on
/// PATH. Generates a short synthetic clip, builds the HLS ladder, and checks the
/// playlists, segments and poster were produced.
/// </summary>
public class TranscodeTests
{
    [FfmpegFact]
    public async Task Hls_ladder_produces_master_renditions_and_poster()
    {
        var ct = TestContext.Current.CancellationToken;
        var work = Path.Combine(Path.GetTempPath(), $"dcms-tx-{Guid.NewGuid():N}");
        Directory.CreateDirectory(work);
        var input = Path.Combine(work, "input.mp4");

        try
        {
            // 3-second 640x360 test pattern with a tone.
            await FFMpegArguments
                .FromFileInput("testsrc=size=640x360:rate=30:duration=3", verifyExists: false,
                    options => options.WithCustomArgument("-f lavfi"))
                .OutputToFile(input, overwrite: true, options => options
                    .WithVideoCodec("libx264")
                    .WithCustomArgument("-pix_fmt yuv420p"))
                .ProcessAsynchronously();

            var transcoder = new MediaWorkerApp::Dcms.MediaWorker.VideoTranscoder();
            var result = await transcoder.TranscodeAsync(input, work, ct);

            var hlsDir = Path.Combine(work, "hls");
            File.Exists(Path.Combine(hlsDir, "master.m3u8")).Should().BeTrue();
            result.Renditions.Should().NotBeEmpty();
            Directory.GetFiles(hlsDir, "*.ts").Should().NotBeEmpty();
            File.Exists(result.PosterPath).Should().BeTrue();
        }
        finally
        {
            try { Directory.Delete(work, recursive: true); } catch { /* best effort */ }
        }
    }
}
