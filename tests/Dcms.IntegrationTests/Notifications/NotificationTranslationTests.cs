extern alias AdminApiApp;
using System.Reflection;
using System.Text.Json;

using NotificationKinds = AdminApiApp::Dcms.AdminApi.Notifications.NotificationKinds;

namespace Dcms.IntegrationTests.Notifications;

/// <summary>
/// Every notification kind must have a title and a body in every locale the SPA ships. No
/// containers needed — this is a source and resource scan.
///
/// <para>It exists because the failure mode is silent and ugly: a missing key makes i18next
/// render the key itself, so the bell shows <c>notifications.kinds.site_published.title</c>
/// where "Site deployed" should be. Nothing throws and no other test would notice.</para>
///
/// <para>It also pins the underscore convention. i18next's key separator is ".", so a kind's
/// dots have to be flattened by <c>NotificationKinds.Slug</c> — a dotted key is resolved as a
/// nested lookup and never matches the flat locale entry. Reverting that would break every
/// notification at once, which is exactly the kind of thing that ships unnoticed.</para>
/// </summary>
public class NotificationTranslationTests
{
    private static readonly string[] Locales = ["en", "cs"];

    public static TheoryData<string, string> KindsAndLocales()
    {
        var data = new TheoryData<string, string>();
        foreach (var kind in AllKinds())
        {
            foreach (var locale in Locales)
            {
                data.Add(kind, locale);
            }
        }
        return data;
    }

    [Theory]
    [MemberData(nameof(KindsAndLocales))]
    public void Every_kind_has_a_title_and_body_in_every_locale(string kind, string locale)
    {
        var kinds = LocaleKinds(locale);
        var slug = NotificationKinds.Slug(kind);

        kinds.TryGetProperty(slug, out var entry).Should().BeTrue(
            "notification kind '{0}' has no entry at notifications.kinds.{1} in {2}/common.json — "
            + "the bell would render the raw i18n key", kind, slug, locale);

        entry.TryGetProperty("title", out var title).Should().BeTrue();
        entry.TryGetProperty("body", out var body).Should().BeTrue();
        title.GetString().Should().NotBeNullOrWhiteSpace();
        body.GetString().Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public void Locale_files_carry_no_kinds_the_catalogue_does_not_define()
    {
        var defined = AllKinds().Select(NotificationKinds.Slug).ToHashSet(StringComparer.Ordinal);

        foreach (var locale in Locales)
        {
            var stale = LocaleKinds(locale).EnumerateObject()
                .Select(p => p.Name)
                .Where(name => !defined.Contains(name))
                .ToList();

            stale.Should().BeEmpty(
                "these entries in {0}/common.json match no kind in NotificationKinds — either a kind "
                + "was renamed, which orphans the notifications already stored under the old one, or "
                + "the strings are dead", locale);
        }
    }

    [Fact]
    public void Slug_flattens_the_dots_i18next_would_otherwise_read_as_nesting()
    {
        NotificationKinds.Slug("site.build.failed").Should().Be("site_build_failed");
        NotificationKinds.Slug(NotificationKinds.SitePublished).Should().NotContain(".");
    }

    /// <summary>Every public kind constant, so adding one to the catalogue enrols it here.</summary>
    private static IEnumerable<string> AllKinds() =>
        typeof(NotificationKinds)
            .GetFields(BindingFlags.Public | BindingFlags.Static | BindingFlags.FlattenHierarchy)
            .Where(f => f is { IsLiteral: true, IsInitOnly: false } && f.FieldType == typeof(string))
            .Select(f => (string)f.GetRawConstantValue()!);

    private static JsonElement LocaleKinds(string locale)
    {
        var path = Path.Combine(RepoRoot(), "apps", "admin", "src", "locales", locale, "common.json");
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        return doc.RootElement.GetProperty("notifications").GetProperty("kinds").Clone();
    }

    private static string RepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Dcms.sln")))
        {
            directory = directory.Parent;
        }
        Assert.SkipWhen(directory is null, "Repository root not found; the source tree is not available here.");
        return directory!.FullName;
    }
}
