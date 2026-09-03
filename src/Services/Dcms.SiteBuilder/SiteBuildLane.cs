using Dcms.Shared.Contracts.Messaging;

namespace Dcms.SiteBuilder;

/// <summary>
/// One queue lane: a durable consumer over some of the publish subjects, draining at its own
/// rate. <see cref="SitePublishConsumer"/> is started once per lane.
///
/// <para><b>Why there is more than one.</b> The three render modes differ by two orders of
/// magnitude — measured on vps1, a Mode C publish is ~340 ms, a Mode A publish ~1 s, and a
/// Mode B publish ~30 s — and they used to share a single consumer. JetStream delivers in
/// order, so a Mode C publish behind a running Mode B build had a p95 of <b>34.8 seconds</b>
/// while its minimum stayed at 338 ms. It was never slow; it was waiting.</para>
///
/// <para><b>Why not just raise the concurrency.</b> Because what is shared is not the
/// consumer, it is the host. A Mode B build runs its install and vite build inside a sandbox
/// container allotted <c>DCMS_BUILD_CPUS</c> (2) of four, so two at once are the whole
/// machine — and a single consumer with two slots could put a Mode B build in both. Separate
/// lanes bound the expensive work independently of the cheap work, which is the actual
/// requirement.</para>
///
/// <para><b>This file is the policy.</b> Which subject sits in which lane is configuration,
/// not wire format: the publisher emits one subject per render mode and nothing downstream
/// assumes a grouping. Moving Mode A into <see cref="Static"/>, or giving it a third lane, is
/// a change here alone.</para>
/// </summary>
/// <param name="DurableName">JetStream durable. Also the <c>consumer_name</c> label on
/// <c>jetstream_consumer_num_pending</c>, so each lane's backlog is separately graphable —
/// which is the measurement that says whether the split is doing its job.</param>
/// <param name="FilterSubjects">Subjects this lane consumes. Must not overlap another lane's:
/// the SITES stream is work-queue retention, and JetStream refuses overlapping filters on
/// one.</param>
/// <param name="Concurrency">Builds this lane runs at once.</param>
/// <param name="WarnOnMessage">Log a warning for each message drained. Set only on the legacy
/// lane, whose whole purpose is to stay empty.</param>
public sealed record SiteBuildLane(
    string DurableName,
    string[] FilterSubjects,
    int Concurrency,
    bool WarnOnMessage = false)
{
    /// <summary>
    /// The git-backed modes. Concurrency 1 by default, and that is a decision: a Mode B
    /// sandbox holds half the host's cores, so two concurrent builds are the whole box.
    /// <c>DCMS_BUILD_CONCURRENCY</c> raises it where there is headroom.
    ///
    /// <para>Mode A shares this lane with Mode B, so a one-second prerender can still wait
    /// behind a thirty-second React build. That is a deliberate default rather than an
    /// oversight — both are "an author published their site from the editor", and it keeps
    /// the lanes one-per-resource rather than one-per-mode. If prerenders waiting behind
    /// React builds ever matters, move <see cref="Subjects.SitePublishRequestedStaticPrerender"/>
    /// into <see cref="Static"/>: Mode A is in-process C# and contends with a Mode C extract
    /// for nothing but this service's own two cores.</para>
    /// </summary>
    public static SiteBuildLane Git() => new(
        "site-builder-git",
        [Subjects.SitePublishRequestedStaticPrerender, Subjects.SitePublishRequestedReactApp],
        ConcurrencyFrom("DCMS_BUILD_CONCURRENCY", 1));

    /// <summary>
    /// Mode C. An extract is I/O against object storage rather than CPU, so it may run more
    /// than one at a time — but not many: <c>ExtractStaticBundleAsync</c> holds the whole
    /// archive in memory, uploads cap at 100 MB, and this container's ceiling is 2 GB. Two is
    /// comfortable; <c>DCMS_BUILD_CONCURRENCY_STATIC</c> raises it if that arithmetic changes.
    /// </summary>
    public static SiteBuildLane Static() => new(
        "site-builder-static",
        [Subjects.SitePublishRequestedStaticFiles],
        ConcurrencyFrom("DCMS_BUILD_CONCURRENCY_STATIC", 2));

    /// <summary>
    /// Drains the pre-split subject. Nothing publishes there any more; this exists so a
    /// message written by an admin-api from before the split still gets built during the
    /// rolling deploy that introduces it, instead of sitting in a work-queue stream that no
    /// filter matches until the build reaper fails it and the author is told their publish
    /// failed for no reason anyone can see.
    ///
    /// <para>Kept as its own lane rather than folded into <see cref="Git"/>, for a reason
    /// worth writing down: the currently running <c>site-builder</c> durable already has
    /// exactly this filter, so binding it under the same name updates it in place and the old
    /// and new processes never disagree about who owns which subject. Adding the legacy
    /// subject to a NEW durable's filter instead would overlap the old durable for as long as
    /// both processes are alive — and a work-queue stream refuses overlapping filters, so
    /// consumer creation would have failed in the middle of the deploy.</para>
    ///
    /// <para>Retire it a release after the split ships: there is a commented
    /// <c>retire_consumer</c> line ready in infra/nats/provision-streams.sh. The warning below
    /// is how you check whether it is still catching anything first.</para>
    /// </summary>
    public static SiteBuildLane Legacy() => new(
        "site-builder",
        [Subjects.SitePublishRequestedLegacy],
        Concurrency: 1,
        WarnOnMessage: true);

    public static IEnumerable<SiteBuildLane> All() => [Git(), Static(), Legacy()];

    private static int ConcurrencyFrom(string variable, int fallback) =>
        int.TryParse(Environment.GetEnvironmentVariable(variable), out var n) && n > 0 ? n : fallback;
}
