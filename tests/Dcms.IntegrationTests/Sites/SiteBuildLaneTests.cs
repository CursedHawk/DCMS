extern alias SiteBuilderApp;
using Dcms.Shared.Contracts.Messaging;
using SiteBuildLane = SiteBuilderApp::Dcms.SiteBuilder.SiteBuildLane;

namespace Dcms.IntegrationTests.Sites;

/// <summary>
/// The two invariants the queue split rests on. Neither is enforced by the compiler, and both
/// fail in ways that are hard to read from the outside: one strands a build forever, the
/// other breaks the deploy.
///
/// <para>(These live here rather than in Dcms.UnitTests only because that project does not
/// reference the service. Nothing below needs Docker.)</para>
/// </summary>
public class SiteBuildLaneTests
{
    private static readonly string[] EverySubjectAPublishCanUse =
    [
        Subjects.SitePublishSubjectFor("StaticFiles"),
        Subjects.SitePublishSubjectFor("StaticPrerender"),
        Subjects.SitePublishSubjectFor("ReactApp"),
        Subjects.SitePublishSubjectFor("a mode nobody has written yet"),
        Subjects.SitePublishRequestedLegacy,
    ];

    [Fact]
    public void Every_subject_a_publish_can_use_is_drained_by_exactly_one_lane()
    {
        // The failure this catches: someone adds a render mode and its subject, and forgets
        // the lane. SITES is a work-queue stream, so a message no consumer's filter matches
        // is never delivered AND never removed — the publish returns 202, the build sits in
        // Queued, and 25 minutes later the reaper marks it Failed with a timeout the author
        // cannot act on. Nothing logs an error at any point.
        foreach (var subject in EverySubjectAPublishCanUse.Distinct())
        {
            var lanes = SiteBuildLane.All().Where(l => l.FilterSubjects.Contains(subject)).ToList();

            lanes.Should().ContainSingle(
                $"'{subject}' must be drained by exactly one lane, and is drained by "
                + $"[{string.Join(", ", lanes.Select(l => l.DurableName))}]");
        }
    }

    [Fact]
    public void No_two_lanes_filter_the_same_subject()
    {
        // JetStream refuses overlapping consumer filters on a work-queue stream, so this is
        // not a style rule: an overlap makes CreateOrUpdateConsumerAsync throw, the consumer
        // loop retries every five seconds forever, and site-builder silently builds nothing.
        // It would surface as a deploy that came up healthy and published no sites.
        var all = SiteBuildLane.All().SelectMany(l => l.FilterSubjects).ToList();

        all.Should().OnlyHaveUniqueItems();
    }

    [Fact]
    public void The_expensive_mode_is_never_alone_with_the_cheap_one()
    {
        // The point of the whole exercise, stated as an assertion: whatever the lanes are
        // rearranged into later, ReactApp must not share a lane with StaticFiles. A Mode B
        // build takes tens of seconds and a Mode C extract takes hundreds of milliseconds,
        // and in-order delivery means sharing a lane puts the second behind the first.
        var react = SiteBuildLane.All().Single(l =>
            l.FilterSubjects.Contains(Subjects.SitePublishRequestedReactApp));

        react.FilterSubjects.Should().NotContain(Subjects.SitePublishRequestedStaticFiles);
    }

    [Fact]
    public void Each_lane_has_a_distinct_durable_and_runs_at_least_one_build()
    {
        var lanes = SiteBuildLane.All().ToList();

        // Durables are also the consumer_name label on jetstream_consumer_num_pending, which
        // is how each lane's backlog is graphed separately.
        lanes.Select(l => l.DurableName).Should().OnlyHaveUniqueItems();
        lanes.Should().OnlyContain(l => l.Concurrency >= 1);
        lanes.Should().OnlyContain(l => l.FilterSubjects.Length >= 1);
    }
}
