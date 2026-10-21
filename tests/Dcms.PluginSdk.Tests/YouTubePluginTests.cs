using System.Text.Json;
using Dcms.Plugins.YouTube;

namespace Dcms.PluginSdk.Tests;

/// <summary>
/// The YouTube plugin's pure parts: what an admin can paste, what the config means, and which
/// videos a sync keeps. Each is a claim that would otherwise only be tested against Google.
/// </summary>
public class YouTubePluginTests
{
    private const string Id = "UCBR8-60-B28hp2BmDPdntcQ";

    [Theory]
    [InlineData("https://www.youtube.com/@Acme", ChannelRefKind.Handle, "Acme")]
    [InlineData("youtube.com/@Acme/videos", ChannelRefKind.Handle, "Acme")]
    [InlineData("https://m.youtube.com/@acme.bakery?si=x", ChannelRefKind.Handle, "acme.bakery")]
    [InlineData("@Acme", ChannelRefKind.Handle, "Acme")]
    [InlineData("Acme", ChannelRefKind.Handle, "Acme")]
    [InlineData("  " + Id + " ", ChannelRefKind.Id, Id)]
    [InlineData("https://www.youtube.com/channel/" + Id, ChannelRefKind.Id, Id)]
    [InlineData("https://www.youtube.com/channel/" + Id + "/featured", ChannelRefKind.Id, Id)]
    [InlineData("https://www.youtube.com/user/acmebakery", ChannelRefKind.Username, "acmebakery")]
    [InlineData("https://www.youtube.com/c/AcmeBakery", ChannelRefKind.Handle, "AcmeBakery")]
    public void A_pasted_channel_resolves_to_what_the_API_can_look_up(string input, ChannelRefKind kind, string value) =>
        YouTubeSource.ParseChannel(input).Should().Be(new ChannelRef(kind, value));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("https://vimeo.com/@acme")]
    [InlineData("https://www.youtube.com/watch?v=dQw4w9WgXcQ")]
    [InlineData("https://www.youtube.com/channel/not-an-id")]
    [InlineData("https://evil.example/youtube.com/@acme")]
    [InlineData("a b")]
    public void Anything_else_is_not_a_channel(string? input) =>
        YouTubeSource.ParseChannel(input).Should().BeNull();

    [Theory]
    [InlineData("https://www.youtube.com/playlist?list=PLrAXtmErZgOeiKm4sgNOknGvNjby9efdf", "PLrAXtmErZgOeiKm4sgNOknGvNjby9efdf")]
    [InlineData("https://www.youtube.com/watch?v=dQw4w9WgXcQ&list=PLrAXtmErZgOeiKm4sgNOknGvNjby9efdf", "PLrAXtmErZgOeiKm4sgNOknGvNjby9efdf")]
    [InlineData("PLrAXtmErZgOeiKm4sgNOknGvNjby9efdf", "PLrAXtmErZgOeiKm4sgNOknGvNjby9efdf")]
    [InlineData("UUBR8-60-B28hp2BmDPdntcQ", "UUBR8-60-B28hp2BmDPdntcQ")]
    public void A_pasted_playlist_resolves_to_its_id(string input, string id) =>
        YouTubeSource.ParsePlaylist(input).Should().Be(id);

    [Theory]
    [InlineData(null)]
    [InlineData("https://www.youtube.com/watch?v=dQw4w9WgXcQ")]
    [InlineData("https://example.com/playlist?list=PLrAXtmErZgOeiKm4sgNOknGvNjby9efdf")]
    [InlineData(Id)]
    [InlineData("short")]
    public void Anything_else_is_not_a_playlist(string? input) =>
        YouTubeSource.ParsePlaylist(input).Should().BeNull();

    [Fact]
    public void Uploads_and_shorts_playlists_derive_from_the_channel_id()
    {
        YouTubeSource.UploadsPlaylist(Id).Should().Be("UUBR8-60-B28hp2BmDPdntcQ");
        YouTubeSource.ShortsPlaylist(Id).Should().Be("UUSHBR8-60-B28hp2BmDPdntcQ");
    }

    [Theory]
    [InlineData("PT4M13S", 253)]
    [InlineData("PT1H2M3S", 3723)]
    [InlineData("P0D", 0)]
    [InlineData("PT59S", 59)]
    [InlineData(null, null)]
    [InlineData("four minutes", null)]
    public void Durations_are_read_as_seconds(string? iso, int? seconds) =>
        YouTubeClient.DurationSeconds(iso).Should().Be(seconds);

    [Fact]
    public void An_unconfigured_instance_has_nothing_to_sync() =>
        YouTubeSettings.Read(JsonDocument.Parse("""{"maxVideos":5,"channel":"  "}""")).Should().BeNull();

    [Fact]
    public void Caps_are_clamped_even_when_the_stored_config_is_out_of_range()
    {
        // The schema refuses these on save; config written before a ceiling changed, or by
        // hand in the database, must still not let one instance spend the deployment's quota.
        Read("""{"channel":"@a","maxVideos":100000}""").MaxVideos.Should().Be(YouTubeSettings.MaxVideosCeiling);
        Read("""{"channel":"@a","maxVideos":0}""").MaxVideos.Should().Be(1);
        Read("""{"channel":"@a","maxVideos":"lots"}""").MaxVideos.Should().Be(YouTubeSettings.DefaultMaxVideos);
        Read("""{"playlist":"PLx","syncSince":"2026-03-01"}""").SyncSince.Should().Be(new DateOnly(2026, 3, 1));
        Read("""{"playlist":"PLx","syncSince":"March"}""").SyncSince.Should().BeNull();
    }

    [Fact]
    public void Shorts_live_and_old_videos_are_kept_only_when_asked_for()
    {
        var defaults = Read("""{"channel":"@a","syncSince":"2026-03-01"}""");
        var everything = Read("""{"channel":"@a","includeShorts":true,"includeLive":true}""");
        var recent = Video(new DateTimeOffset(2026, 3, 1, 0, 30, 0, TimeSpan.Zero));
        var old = Video(new DateTimeOffset(2026, 2, 28, 23, 59, 0, TimeSpan.Zero));

        YouTubeSync.Keep(recent, "video", defaults).Should().BeTrue();
        YouTubeSync.Keep(recent, "short", defaults).Should().BeFalse();
        YouTubeSync.Keep(recent, "live", defaults).Should().BeFalse();
        YouTubeSync.Keep(old, "video", defaults).Should().BeFalse("it predates syncSince");

        YouTubeSync.Keep(recent, "short", everything).Should().BeTrue();
        YouTubeSync.Keep(recent, "live", everything).Should().BeTrue();
        YouTubeSync.Keep(old, "video", everything).Should().BeTrue();
    }

    [Fact]
    public void The_public_config_endpoint_serves_only_the_display_name() =>
        new YouTubePlugin().Manifest.PublicConfigKeys.Should().Equal(YouTubeSettings.ChannelTitleKey);

    [Fact]
    public void The_thumbnail_is_marked_as_an_image_so_Mode_D_can_bind_one_to_it()
    {
        // The visual builder offers a field to an Image only when the plugin says it holds one;
        // the thumbnail is YouTube's URL (Text), so the reference is the only thing that says so.
        var thumbnail = new YouTubePlugin().Manifest.ContentTypes.Single().Fields.Single(f => f.Name == "thumbnail");
        thumbnail.Type.Should().Be(Dcms.PluginSdk.Abstractions.ContentFieldType.Text);
        thumbnail.Reference?.MediaCategory.Should().Be(Dcms.PluginSdk.Abstractions.MediaCategory.Image);
    }

    private static YouTubeSettings Read(string json) => YouTubeSettings.Read(JsonDocument.Parse(json))!;

    private static VideoInfo Video(DateTimeOffset at) =>
        new("dQw4w9WgXcQ", "t", "d", at, "c", null, 60, 1, 1, Live: false);
}
