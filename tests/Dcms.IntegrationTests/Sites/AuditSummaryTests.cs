extern alias AdminApiApp;
using System.Text.Json;
using Dcms.Shared.Sites;
using AuditSummary = AdminApiApp::Dcms.AdminApi.Sites.Audits.AuditSummary;

namespace Dcms.IntegrationTests.Sites;

/// <summary>What a site's people and agent get from one PageSpeed Insights run (#649) — pure, no infrastructure.</summary>
public class AuditSummaryTests
{
    [Fact]
    public void A_run_becomes_its_scores_metrics_field_data_and_the_audits_that_failed()
    {
        using var psi = JsonDocument.Parse(PageSpeedFake.Response("https://example.test/"));
        var report = AuditSummary.From(psi.RootElement, "mobile");

        report.Strategy.Should().Be("mobile");
        report.FinalUrl.Should().Be("https://example.test/");
        report.Scores.Should().Equal(new Dictionary<string, int?>
        {
            ["performance"] = 62, ["accessibility"] = 88, ["best-practices"] = 100, ["seo"] = 90,
        });
        report.Metrics.Select(m => (m.Id, m.DisplayValue)).Should().Equal(
            ("first-contentful-paint", "2.1 s"), ("largest-contentful-paint", "4.8 s"), ("total-blocking-time", "120 ms"),
            ("cumulative-layout-shift", "0.01"), ("speed-index", "3.2 s"));
        report.FieldCategory.Should().Be("FAST");
        report.Field.Select(f => f.Id).Should().Equal("LARGEST_CONTENTFUL_PAINT_MS", "CUMULATIVE_LAYOUT_SHIFT_SCORE");

        // Failing only, metrics aside, by category and then by what fixing them saves.
        report.Issues.Select(i => (i.Category, i.Id)).Should().Equal(
            ("performance", "render-blocking-resources"), ("performance", "uses-optimized-images"),
            ("accessibility", "image-alt"), ("best-practices", "errors-in-console"), ("seo", "meta-description"));
        var images = report.Issues.Single(i => i.Id == "uses-optimized-images");
        images.SavingsBytes.Should().Be(122880);
        images.Items.Should().ContainSingle().Which.Url.Should().Be("https://example.test/hero.jpg");
        report.Issues.Single(i => i.Id == "image-alt").Items.Single().Selector.Should().Be("main > img");
        // A console error is its message and where it came from.
        report.Issues.Single(i => i.Id == "errors-in-console").Items.Should().ContainSingle().Which.Should().Match<AdminApiApp::Dcms.AdminApi.Sites.Audits.AuditItem>(
            x => x.Url == "https://api.example.test/items" && x.Label == "Failed to load resource: net::ERR_FAILED");
    }

    [Theory]
    [InlineData("audit-0123456789abcdef0123456789abcdef.dcms.highgeek.eu", "0123456789abcdef0123456789abcdef")]
    [InlineData("audit-0123456789ABCDEF0123456789abcdef.dcms.highgeek.eu", null)]
    [InlineData("audit-short.dcms.highgeek.eu", null)]
    [InlineData("www.audit-0123456789abcdef0123456789abcdef.example", null)]
    [InlineData("example.com", null)]
    [InlineData("audit-0123456789abcdef0123456789abcdef.evil.example", null)]
    [InlineData("audit-0123456789abcdef0123456789abcdef.x.dcms.highgeek.eu", null)]
    public void Only_an_audit_builds_own_address_under_the_managed_zone_names_a_token(string host, string? token) =>
        DomainResolver.AuditToken(host, "dcms.highgeek.eu").Should().Be(token);
}
