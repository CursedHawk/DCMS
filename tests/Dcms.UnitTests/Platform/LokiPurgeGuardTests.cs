using Dcms.PlatformApi.Observability;

namespace Dcms.UnitTests.Platform;

/// <summary>
/// The guard between an operator with a delete button and the audit evidence.
///
/// <para><c>loki.yaml</c> holds <c>{service="admin-api", category="audit"}</c> and
/// <c>{category="security"}</c> for 90 days deliberately — the Critical <c>AUDIT ANCHOR</c>
/// lines are one of the three audit-integrity controls the runbook names. Loki's delete API has
/// no concept of a protected stream, so every one of these assertions is the only thing
/// enforcing that.</para>
/// </summary>
public class LokiPurgeGuardTests
{
    [Theory]
    [InlineData("{service=\"admin-api\"}")]
    [InlineData("{container=\"dcms-content-api-1\"}")]
    [InlineData("{service=\"site-builder\", stream=\"stderr\"}")]
    public void Guard_excludes_both_protected_categories(string selector)
    {
        var guarded = LokiClient.Guard(selector);

        guarded.Should().Contain("category!=\"audit\"");
        guarded.Should().Contain("category!=\"security\"");
    }

    [Fact]
    public void Guard_keeps_the_caller_matchers()
    {
        LokiClient.Guard("{service=\"admin-api\", stream=\"stdout\"}")
            .Should().Be("{service=\"admin-api\", stream=\"stdout\", category!=\"audit\", category!=\"security\"}");
    }

    [Fact]
    public void Guard_refuses_a_selector_matching_everything()
    {
        // {} is valid LogQL for "every stream", and a purge of every stream over a wide window
        // is the single most destructive call this API can make.
        var act = () => LokiClient.Guard("{}");
        act.Should().Throw<ArgumentException>().WithMessage("*every stream*");
    }

    [Theory]
    [InlineData("service=\"admin-api\"")]
    [InlineData("{service=\"admin-api\"")]
    [InlineData("")]
    public void Guard_refuses_anything_that_is_not_a_stream_selector(string selector)
    {
        // Refused rather than repaired: guessing at what a malformed selector meant is how a
        // purge deletes something nobody asked for.
        var act = () => LokiClient.Guard(selector);
        act.Should().Throw<ArgumentException>();
    }

    [Theory]
    [InlineData("{category=\"audit\"}")]
    [InlineData("{service=\"admin-api\", category=\"audit\"}")]
    [InlineData("{category=\"security\"}")]
    [InlineData("{category=~\"audit\"}")]
    [InlineData("{CATEGORY=\"Audit\"}")]
    public void Aiming_at_a_protected_stream_is_detected(string selector)
    {
        LokiClient.TargetsProtectedStream(selector).Should().BeTrue();
    }

    [Theory]
    [InlineData("{service=\"admin-api\"}")]
    [InlineData("{category=\"http\"}")]
    [InlineData("{container=\"dcms-loki-1\"}")]
    public void An_ordinary_selector_is_not_flagged(string selector)
    {
        LokiClient.TargetsProtectedStream(selector).Should().BeFalse();
    }

    [Fact]
    public void A_guarded_selector_still_excludes_protected_streams_when_the_caller_negates_the_label()
    {
        // A caller who writes category!="something" has not thereby opened the protected
        // streams: the guard's own exclusions are appended regardless of what came before.
        var guarded = LokiClient.Guard("{service=\"admin-api\", category!=\"http\"}");

        guarded.Should().Contain("category!=\"audit\"");
        guarded.Should().Contain("category!=\"security\"");
    }
}
