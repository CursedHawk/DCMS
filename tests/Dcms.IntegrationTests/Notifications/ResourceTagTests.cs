extern alias AdminApiApp;

using AdminApiApp::Dcms.AdminApi.Notifications;
using System.Reflection;

namespace Dcms.IntegrationTests.Notifications;

/// <summary>
/// The tag vocabulary is duplicated across the wire — <c>ResourceTags</c> here, the
/// <c>RESOURCE_TAGS</c> array in the SPA's <c>live</c> module — and that duplication is
/// deliberate: a tag either side does not recognise degrades to "nothing refetches", which is
/// the right failure for a console that may be older or newer than the server.
///
/// <para>What is <i>not</i> tolerable is a notification kind whose tag names a class of data
/// that does not exist, because that fails silently in exactly the same way as a console being
/// out of date, and nobody would look for it. These tests hold the server side honest.</para>
/// </summary>
public sealed class ResourceTagTests
{
    private static IReadOnlyList<string> AllTags() =>
        typeof(ResourceTags)
            .GetFields(BindingFlags.Public | BindingFlags.Static | BindingFlags.FlattenHierarchy)
            .Where(f => f is { IsLiteral: true, IsInitOnly: false } && f.FieldType == typeof(string))
            .Select(f => (string)f.GetRawConstantValue()!)
            .ToList();

    private static IReadOnlyList<string> AllKinds() =>
        typeof(NotificationKinds)
            .GetFields(BindingFlags.Public | BindingFlags.Static | BindingFlags.FlattenHierarchy)
            .Where(f => f is { IsLiteral: true, IsInitOnly: false } && f.FieldType == typeof(string))
            .Select(f => (string)f.GetRawConstantValue()!)
            .ToList();

    [Fact]
    public void Every_kind_maps_to_a_real_tag_or_to_nothing()
    {
        var tags = AllTags();

        foreach (var kind in AllKinds())
        {
            var tag = ResourceTags.ForKind(kind);
            if (tag is null) continue;

            tags.Should().Contain(
                tag,
                $"the '{kind}' notification maps to resource tag '{tag}', which is not declared "
                + "in ResourceTags — the SPA would ignore it and the screen would stay stale");
        }
    }

    [Fact]
    public void Tags_are_lowercase_single_words()
    {
        // They are compared verbatim against a hand-written list in the SPA. A stray capital
        // or a space is a mismatch that shows up as "the table just does not update".
        foreach (var tag in AllTags())
        {
            tag.Should().MatchRegex("^[a-z]+$", $"'{tag}' has to match the SPA's copy exactly");
        }
    }

    [Fact]
    public void An_unknown_kind_maps_to_nothing_rather_than_guessing()
    {
        ResourceTags.ForKind("something.nobody.declared").Should().BeNull();
    }

    [Fact]
    public void The_kinds_that_change_a_visible_list_all_have_a_tag()
    {
        // The explicit opposite of the mapping: these are the ones where a stale screen would
        // be noticed and complained about, so a null here is a regression rather than a choice.
        string[] mustPush =
        [
            NotificationKinds.SitePublished,
            NotificationKinds.SiteBuildFailed,
            NotificationKinds.MediaProcessed,
            NotificationKinds.MediaFailed,
            NotificationKinds.ContentPublished,
            NotificationKinds.ContentUnpublished,
            NotificationKinds.FormSubmitted,
            NotificationKinds.PluginInstanceChanged,
            NotificationKinds.DomainVerified,
            NotificationKinds.MemberRoleChanged,
        ];

        foreach (var kind in mustPush)
        {
            ResourceTags.ForKind(kind).Should().NotBeNull(
                $"'{kind}' changes something a console is likely to have open");
        }
    }
}
