using System.Text.Json;
using FFMpegCore;
using FFMpegCore.Pipes;

namespace Dcms.MediaWorker;

public sealed record AudioTranscodeResult(string AacPath, byte[] PeaksJson, TimeSpan Duration);

/// <summary>
/// Normalizes audio to AAC (m4a) and produces a coarse waveform-peaks JSON for
/// the player UI (downsampled absolute amplitudes from a mono PCM decode).
/// </summary>
public sealed class AudioTranscoder
{
    private const int PeakBuckets = 400;

    public async Task<AudioTranscodeResult> TranscodeAsync(string inputFile, string workDir, CancellationToken ct)
    {
        var probe = await FFProbe.AnalyseAsync(inputFile, cancellationToken: ct);
        var duration = probe.Duration;

        var aac = Path.Combine(workDir, "audio.m4a");
        await FFMpegArguments
            .FromFileInput(inputFile)
            .OutputToFile(aac, overwrite: true, options => options
                .WithAudioCodec("aac")
                .WithAudioBitrate(192)
                .WithCustomArgument("-vn"))
            .CancellableThrough(ct)
            .ProcessAsynchronously();

        var peaks = await ComputePeaksAsync(inputFile, ct);
        var json = JsonSerializer.SerializeToUtf8Bytes(new { version = 1, buckets = PeakBuckets, peaks });
        return new AudioTranscodeResult(aac, json, duration);
    }

    // Decode to mono 8kHz PCM s16le and downsample to PeakBuckets max-amplitude buckets.
    private static async Task<float[]> ComputePeaksAsync(string inputFile, CancellationToken ct)
    {
        using var pcm = new MemoryStream();
        await FFMpegArguments
            .FromFileInput(inputFile)
            .OutputToPipe(new StreamPipeSink(pcm), options => options
                .WithAudioCodec("pcm_s16le")
                .WithCustomArgument("-ac 1")
                .WithCustomArgument("-ar 8000")
                .ForceFormat("s16le"))
            .CancellableThrough(ct)
            .ProcessAsynchronously();

        var bytes = pcm.ToArray();
        var sampleCount = bytes.Length / 2;
        if (sampleCount == 0)
        {
            return [];
        }

        var peaks = new float[PeakBuckets];
        var perBucket = Math.Max(1, sampleCount / PeakBuckets);
        for (var i = 0; i < sampleCount; i++)
        {
            var sample = Math.Abs(BitConverter.ToInt16(bytes, i * 2)) / 32768f;
            var bucket = Math.Min(PeakBuckets - 1, i / perBucket);
            if (sample > peaks[bucket])
            {
                peaks[bucket] = sample;
            }
        }
        return peaks;
    }
}
