using Dcms.Shared.Data.Media;

namespace Dcms.UnitTests.Media;

public class WebpLadderTests
{
    [Theory]
    // A picture 1500px wide has no 1920: the widest it has is the sharpest there is.
    [InlineData("webp-1920", "webp-320,webp-640,webp-1280,thumb", "webp-1280")]
    // A width the ladder never had: the next one up, not a blurrier one.
    [InlineData("webp-960", "webp-320,webp-640,webp-1280,webp-1920,thumb", "webp-1280")]
    // A picture narrower than 320 is made once, at its own width.
    [InlineData("webp-640", "webp-200,thumb", "webp-200")]
    [InlineData("webp-640", "thumb", null)]
    [InlineData("thumb", "webp-320", null)]
    [InlineData("webp-abc", "webp-320", null)]
    public void Nearest_serves_the_closest_width_that_is_not_blurrier(string requested, string available, string? expected) =>
        WebpLadder.Nearest(requested, available.Split(',')).Should().Be(expected);
}
