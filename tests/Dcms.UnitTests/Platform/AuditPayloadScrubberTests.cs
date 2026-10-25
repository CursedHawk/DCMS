using System.Text.Json.Nodes;
using Dcms.PlatformApi.Reporting;

namespace Dcms.UnitTests.Platform;

/// <summary>Metadata and hand-written changes skip the redactor on the way in; this is the net on the way out.</summary>
public sealed class AuditPayloadScrubberTests
{
    [Fact]
    public void Sensitive_metadata_keys_lose_their_values_at_any_depth()
    {
        var scrubbed = JsonNode.Parse(AuditPayloadScrubber.Scrub(
            """{"client_id":"dcms-console","client_secret":"s3cret","nested":{"apiKey":"k","rows":3},"list":[{"password":"p"}]}""")!)!;

        scrubbed["client_id"]!.GetValue<string>().Should().Be("dcms-console");
        scrubbed["client_secret"]!.GetValue<string>().Should().Be(AuditPayloadScrubber.Withheld);
        scrubbed["nested"]!["apiKey"]!.GetValue<string>().Should().Be(AuditPayloadScrubber.Withheld);
        scrubbed["nested"]!["rows"]!.GetValue<int>().Should().Be(3);
        scrubbed["list"]![0]!["password"]!.GetValue<string>().Should().Be(AuditPayloadScrubber.Withheld);
    }

    [Fact]
    public void A_change_to_a_sensitive_field_is_shown_as_redacted()
    {
        var scrubbed = JsonNode.Parse(AuditPayloadScrubber.Scrub(
            """[{"field":"ClientSecret","before":"old","after":"new","redacted":false},{"field":"Name","before":"a","after":"b","redacted":false}]""")!)!;

        scrubbed[0]!["before"].Should().BeNull();
        scrubbed[0]!["after"].Should().BeNull();
        scrubbed[0]!["redacted"]!.GetValue<bool>().Should().BeTrue();
        scrubbed[1]!["after"]!.GetValue<string>().Should().Be("b");
    }

    [Fact]
    public void A_payload_that_cannot_be_vetted_is_withheld_whole()
    {
        AuditPayloadScrubber.Scrub("token=abc; not json").Should().Contain(AuditPayloadScrubber.Withheld).And.NotContain("abc");
        AuditPayloadScrubber.Scrub(null).Should().BeNull();
    }
}
