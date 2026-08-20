using System.Text.Json;
using Dcms.Shared.Contracts.Events;

namespace Dcms.UnitTests.Messaging;

/// <summary>
/// `AnalyticsEnabled` travels on the publish message because site-builder cannot
/// answer the question itself — it connects as a least-privilege role that reaches
/// the `sites` schema alone, so its own read of `plugins.plugin_instances` failed
/// with 42501 on every publish and silently fell back to "enabled". That fallback
/// was invisible for a whole release: publishes succeeded, and the only symptom was
/// a log line nobody was reading, while every site shipped a cookie banner whether
/// or not it recorded anything.
///
/// So the two things that made it invisible are pinned here: the field survives the
/// wire, and an absent field still means "assume enabled" rather than the `false`
/// a bare `default` would give — which would silently drop the banner from sites
/// that genuinely need one, the same bug with the consent flipped.
/// </summary>
public class SitePublishRequestedTests
{
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    private static SitePublishRequested Sample(bool? analytics) => new(
        Guid.NewGuid(), DateTimeOffset.UtcNow, Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
        "StaticPrerender", analytics);

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Carries_the_analytics_flag_across_the_wire(bool analytics)
    {
        var json = JsonSerializer.Serialize(Sample(analytics), Web);
        var round = JsonSerializer.Deserialize<SitePublishRequested>(json, Web)!;

        round.AnalyticsEnabled.Should().Be(analytics);
    }

    [Fact]
    public void Reads_a_message_published_before_the_field_existed()
    {
        // Exactly the payload the old producer wrote. A message can be sitting in
        // the stream across the deploy that adds the field, so this has to decode.
        const string legacy = """
            {
              "eventId": "0b3f0f6e-2b1a-4a1e-9b3a-2f4c6d8e0a11",
              "occurredAt": "2026-08-19T22:00:00+00:00",
              "tenantId": "1a2b3c4d-5e6f-4a7b-8c9d-0e1f2a3b4c5d",
              "siteId": "2a2b3c4d-5e6f-4a7b-8c9d-0e1f2a3b4c5d",
              "buildId": "3a2b3c4d-5e6f-4a7b-8c9d-0e1f2a3b4c5d",
              "renderMode": "StaticPrerender"
            }
            """;

        var job = JsonSerializer.Deserialize<SitePublishRequested>(legacy, Web)!;

        job.RenderMode.Should().Be("StaticPrerender");
        // Not stated — and the consumer reads that as enabled, so an old message
        // errs towards asking for consent rather than tracking without it.
        job.AnalyticsEnabled.Should().BeNull();
        (job.AnalyticsEnabled ?? true).Should().BeTrue();
    }
}
