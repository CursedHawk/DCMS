using System.Diagnostics;
using System.Runtime.CompilerServices;

namespace Dcms.IntegrationTests;

/// <summary>
/// Fact that self-skips when ffmpeg/ffprobe are not on PATH (transcoding tests).
/// CI runners and the media-worker image carry ffmpeg; dev machines may not.
/// </summary>
public sealed class FfmpegFactAttribute : FactAttribute
{
    private static readonly Lazy<bool> Available = new(Probe);

    public FfmpegFactAttribute(
        [CallerFilePath] string? sourceFilePath = null,
        [CallerLineNumber] int sourceLineNumber = -1)
        : base(sourceFilePath, sourceLineNumber)
    {
        if (!Available.Value)
        {
            Skip = "ffmpeg/ffprobe not available on PATH.";
        }
    }

    private static bool Probe()
    {
        // ffprobe must exist, and ffmpeg must be a full build (some distributions
        // ship an audio-only ffmpeg without libx264 / video filters).
        return ToolRuns("ffprobe", "-version", null)
               && ToolRuns("ffmpeg", "-hide_banner -encoders", "libx264");
    }

    private static bool ToolRuns(string tool, string args, string? mustContain)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo(tool, args)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            });
            if (process is null)
            {
                return false;
            }
            var output = process.StandardOutput.ReadToEnd();
            process.WaitForExit(5000);
            return mustContain is null || output.Contains(mustContain, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }
}
