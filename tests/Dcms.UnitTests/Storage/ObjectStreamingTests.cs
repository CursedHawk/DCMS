using Dcms.Shared.Storage;

namespace Dcms.UnitTests.Storage;

/// <summary>
/// PERF-01: delivery now answers Range requests by pulling only the asked-for bytes from MinIO,
/// so this arithmetic is what decides which bytes a client receives. A wrong bound here serves a
/// video the wrong slice or 416s a valid seek — silently, from every tenant site.
/// </summary>
public class ObjectStreamingTests
{
    private const long Size = 1000;

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("items=0-10")]      // not a bytes range
    [InlineData("bytes=0-10,20-30")] // multi-range: served whole, as RFC 9110 permits
    [InlineData("bytes=abc-def")]
    [InlineData("bytes=10-5")]       // end before start is malformed, not unsatisfiable
    public void Serves_the_whole_object_when_there_is_no_usable_single_range(string? header)
    {
        ObjectStreaming.ResolveRange(header, Size, out _).Should().Be(RangeOutcome.Whole);
    }

    [Fact]
    public void Resolves_a_closed_range()
    {
        ObjectStreaming.ResolveRange("bytes=0-499", Size, out var r).Should().Be(RangeOutcome.Partial);
        r.Should().Be(new ByteRange(0, 499));
        r.Length.Should().Be(500);
    }

    [Fact]
    public void Resolves_an_open_ended_range_to_the_last_byte()
    {
        // The shape a video player sends on seek.
        ObjectStreaming.ResolveRange("bytes=500-", Size, out var r).Should().Be(RangeOutcome.Partial);
        r.Should().Be(new ByteRange(500, 999));
    }

    [Fact]
    public void Resolves_a_suffix_range_to_the_LAST_bytes()
    {
        // bytes=-100 is the final 100 bytes — not bytes 0..100. Getting this backwards serves the
        // file's head to a client asking for its tail (an MP4 moov atom, say).
        ObjectStreaming.ResolveRange("bytes=-100", Size, out var r).Should().Be(RangeOutcome.Partial);
        r.Should().Be(new ByteRange(900, 999));
    }

    [Fact]
    public void Clamps_a_suffix_larger_than_the_object_to_the_whole_object()
    {
        ObjectStreaming.ResolveRange("bytes=-5000", Size, out var r).Should().Be(RangeOutcome.Partial);
        r.Should().Be(new ByteRange(0, 999));
    }

    [Fact]
    public void Clamps_an_end_past_the_object_to_the_last_byte()
    {
        ObjectStreaming.ResolveRange("bytes=900-5000", Size, out var r).Should().Be(RangeOutcome.Partial);
        r.Should().Be(new ByteRange(900, 999));
    }

    [Theory]
    [InlineData("bytes=1000-")]      // start == size: first byte that does not exist
    [InlineData("bytes=5000-6000")]
    public void Refuses_a_range_that_starts_past_the_end(string header)
    {
        ObjectStreaming.ResolveRange(header, Size, out _).Should().Be(RangeOutcome.Unsatisfiable);
    }

    [Fact]
    public void An_empty_object_satisfies_no_range()
    {
        ObjectStreaming.ResolveRange("bytes=0-", 0, out _).Should().Be(RangeOutcome.Unsatisfiable);
    }

    [Fact]
    public void The_single_byte_range_is_one_byte_long()
    {
        ObjectStreaming.ResolveRange("bytes=0-0", Size, out var r).Should().Be(RangeOutcome.Partial);
        r.Length.Should().Be(1);
    }
}
