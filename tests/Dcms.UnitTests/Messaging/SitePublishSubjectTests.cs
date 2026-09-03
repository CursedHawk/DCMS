using Dcms.Shared.Contracts.Messaging;

namespace Dcms.UnitTests.Messaging;

/// <summary>
/// The publish subject is chosen from the render mode, and that choice is what keeps a
/// 340 ms bundle extract from waiting behind a 30-second React build. Before the split all
/// three shared one subject and one consumer, and JetStream delivers in order: a Mode C
/// publish behind a running Mode B build measured a p95 of 34.8 seconds on vps1 while its
/// minimum stayed at 338 ms.
///
/// These pin the routing rather than the lanes — which subject a lane consumes is
/// site-builder's configuration, and is covered separately by SiteBuildLaneTests.
/// </summary>
public class SitePublishSubjectTests
{
    [Theory]
    [InlineData("StaticFiles", Subjects.SitePublishRequestedStaticFiles)]
    [InlineData("StaticPrerender", Subjects.SitePublishRequestedStaticPrerender)]
    [InlineData("ReactApp", Subjects.SitePublishRequestedReactApp)]
    public void Routes_each_render_mode_to_its_own_subject(string mode, string expected) =>
        Subjects.SitePublishSubjectFor(mode).Should().Be(expected);

    [Theory]
    [InlineData("staticfiles")]
    [InlineData("STATICFILES")]
    [InlineData("StaticFILES")]
    public void Matches_the_mode_case_insensitively(string mode)
    {
        // The mode travels as a string, not the enum, so nothing in the type system stops a
        // producer writing it in a different case. Routing on an exact match would send those
        // to the wrong lane silently — they would still build correctly, just slowly.
        Subjects.SitePublishSubjectFor(mode).Should().Be(Subjects.SitePublishRequestedStaticFiles);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("SomethingAddedLater")]
    public void Sends_an_unrecognised_mode_to_the_prerender_subject(string? mode)
    {
        // Mirrors what the builder does with it: BuildAsync dispatches ReactApp and
        // StaticFiles explicitly and treats everything else as Mode A, so the message ends up
        // where the code that handles it lives. The alternative — defaulting to the cheap
        // lane — would put a build of unknown cost in front of the ones that lane exists to
        // keep fast, which is the exact failure the split was made to remove.
        Subjects.SitePublishSubjectFor(mode).Should().Be(Subjects.SitePublishRequestedStaticPrerender);
    }

    [Fact]
    public void Every_subject_still_lives_under_the_streams_wildcard()
    {
        // SITES is provisioned with the filter `site.publish.>` (infra/nats/provision-streams.sh).
        // A subject outside it is accepted by the publisher and stored by nothing: the publish
        // succeeds, no consumer ever sees it, and the build sits in Queued until the reaper
        // fails it. That is why the split needed no stream change, and why it must stay true.
        string[] all =
        [
            Subjects.SitePublishRequestedStaticFiles,
            Subjects.SitePublishRequestedStaticPrerender,
            Subjects.SitePublishRequestedReactApp,
            Subjects.SitePublishRequestedLegacy,
        ];

        all.Should().OnlyContain(s => s.StartsWith("site.publish.", StringComparison.Ordinal));
        all.Should().OnlyHaveUniqueItems();
    }
}
