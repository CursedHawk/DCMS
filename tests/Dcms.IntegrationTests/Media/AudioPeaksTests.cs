extern alias MediaWorkerApp;

using MediaWorkerApp::Dcms.MediaWorker;

namespace Dcms.IntegrationTests.Media;

/// <summary>
/// The waveform for the audio player. A full-scale negative sample (-32768) used to throw on
/// Math.Abs(short) and fail the whole import of any loudly mastered WAV (2026-10-06).
/// </summary>
public class AudioPeaksTests
{
    [Fact]
    public void A_full_scale_negative_sample_is_a_full_peak()
    {
        var pcm = new byte[4];
        BitConverter.TryWriteBytes(pcm.AsSpan(0, 2), short.MinValue);
        BitConverter.TryWriteBytes(pcm.AsSpan(2, 2), (short)16384);

        var peaks = AudioTranscoder.PeaksOf(pcm);

        peaks.Max().Should().Be(1f);
        peaks.Should().OnlyContain(p => p >= 0f && p <= 1f);
    }

    [Fact]
    public void No_samples_is_no_waveform() => AudioTranscoder.PeaksOf([]).Should().BeEmpty();
}
