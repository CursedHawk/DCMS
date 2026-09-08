using Dcms.PlatformApi.Observability;

namespace Dcms.UnitTests.Platform;

/// <summary>
/// The joins behind the console's "right now" table.
///
/// <para>Every case here is one where the wrong answer renders as a perfectly ordinary number.
/// A p95 of nothing shown as 0 ms says the platform is fast; an error ratio taken over no
/// traffic says it is broken; a service present in one recording rule and missing from another
/// simply disappears. None of them look like a bug on the page.</para>
/// </summary>
public sealed class HealthSignalShapeTests
{
    private static (IReadOnlyDictionary<string, string>, double) Row(string service, double value) =>
        (new Dictionary<string, string>(StringComparer.Ordinal) { ["service"] = service }, value);

    private static (IReadOnlyDictionary<string, string>, double) Target(string job, string instance, bool up) =>
        (new Dictionary<string, string>(StringComparer.Ordinal) { ["job"] = job, ["instance"] = instance },
         up ? 1d : 0d);

    [Fact]
    public void Joins_the_three_rules_into_one_row_per_service()
    {
        var result = PlatformHealthEndpoints.Shape(
            reachable: true,
            requests: [Row("admin-api", 8)],
            errors: [Row("admin-api", 0.4)],
            latency: [Row("admin-api", 0.12)],
            targets: []);

        var service = result.Services.Should().ContainSingle().Subject;
        service.RequestsPerSecond.Should().Be(8);
        service.ErrorsPerSecond.Should().Be(0.4);
        service.ErrorRatio.Should().BeApproximately(0.05, 1e-9);
        service.P95Seconds.Should().Be(0.12);
    }

    /// <summary>
    /// histogram_quantile over an empty rate is NaN. Reported as zero it would read as the
    /// fastest service on the platform — for a service serving nothing at all.
    /// </summary>
    [Fact]
    public void A_p95_of_nothing_is_no_answer_rather_than_zero()
    {
        var result = PlatformHealthEndpoints.Shape(
            reachable: true,
            requests: [Row("email-worker", 0)],
            errors: [],
            latency: [Row("email-worker", double.NaN)],
            targets: []);

        result.Services.Single().P95Seconds.Should().BeNull();
    }

    /// <summary>
    /// The recording rule clamps its denominator so an alert expression never sees a NaN, which
    /// turns an idle service's ratio into an enormous number. The console recomputes instead.
    /// </summary>
    [Fact]
    public void A_service_with_no_traffic_has_no_error_ratio()
    {
        var result = PlatformHealthEndpoints.Shape(
            reachable: true,
            requests: [Row("site-host", 0)],
            errors: [Row("site-host", 0)],
            latency: [],
            targets: []);

        result.Services.Single().ErrorRatio.Should().Be(0);
    }

    [Fact]
    public void A_service_only_the_error_rule_knows_about_is_still_reported()
    {
        var result = PlatformHealthEndpoints.Shape(
            reachable: true,
            requests: [],
            errors: [Row("content-api", 2)],
            latency: [],
            targets: []);

        result.Services.Should().ContainSingle(s => s.Service == "content-api" && s.ErrorsPerSecond == 2);
    }

    [Fact]
    public void The_worst_service_is_first()
    {
        var result = PlatformHealthEndpoints.Shape(
            reachable: true,
            requests: [Row("a", 10), Row("b", 10)],
            errors: [Row("a", 0.1), Row("b", 5)],
            latency: [],
            targets: []);

        result.Services.Select(s => s.Service).Should().Equal("b", "a");
    }

    /// <summary>This list exists to be scanned for the thing that is broken.</summary>
    [Fact]
    public void Down_targets_come_first()
    {
        var result = PlatformHealthEndpoints.Shape(
            reachable: true,
            requests: [],
            errors: [],
            latency: [],
            targets: [Target("prometheus", "localhost:9090", true), Target("nats", "nats:7777", false)]);

        result.Targets.Select(t => t.Job).Should().Equal("nats", "prometheus");
        result.Targets[0].Up.Should().BeFalse();
    }

    /// <summary>
    /// Reachability is carried through rather than inferred from emptiness: "nothing is
    /// happening" and "nothing is being measured" are opposite conclusions.
    /// </summary>
    [Fact]
    public void An_unreachable_prometheus_says_so_rather_than_looking_idle()
    {
        var result = PlatformHealthEndpoints.Shape(
            reachable: false, requests: [], errors: [], latency: [], targets: []);

        result.Reachable.Should().BeFalse();
        result.Services.Should().BeEmpty();
    }

    [Fact]
    public void A_series_with_no_service_label_is_skipped_rather_than_named_blank()
    {
        var unlabelled = (IReadOnlyDictionary<string, string>)new Dictionary<string, string>();

        var result = PlatformHealthEndpoints.Shape(
            reachable: true,
            requests: [(unlabelled, 5)],
            errors: [(unlabelled, 5)],
            latency: [],
            targets: []);

        result.Services.Should().BeEmpty();
    }
}
