using Dcms.Shared.Data.Sites;

namespace Dcms.UnitTests.Data;

/// <summary>What a site's people set for search engines (#648): its rules, and what may be stored.</summary>
public class SiteSearchSettingsTests
{
    [Theory]
    [InlineData("/about", "/about", true)]
    [InlineData("/about", "/About/", true)]
    [InlineData("/about", "/about/team", false)]
    [InlineData("/blog/:slug", "/blog/hello", true)]
    [InlineData("/blog/:slug", "/blog", false)]
    [InlineData("/blog/:slug", "/blog/a/b", false)]
    [InlineData("/private/*", "/private", true)]
    [InlineData("/private/*", "/private/a/b", true)]
    [InlineData("/private/*", "/privateer", false)]
    [InlineData("/*", "/", true)]
    [InlineData("/", "/", true)]
    [InlineData("/", "/about", false)]
    public void A_rule_covers_its_page_its_items_or_its_section(string rule, string path, bool covered) =>
        SiteSearchSettings.Matches(rule, path).Should().Be(covered);

    [Fact]
    public void The_defaults_store_nothing_and_anything_unreadable_reads_as_the_defaults()
    {
        SiteSearchSettings.Default.ToJson().Should().BeNull();
        SiteSearchSettings.Parse(null).Should().BeSameAs(SiteSearchSettings.Default);
        SiteSearchSettings.Parse("not json").Should().BeSameAs(SiteSearchSettings.Default);

        var settings = new SiteSearchSettings { Indexable = false, Exclude = ["/drafts/*"], Include = ["/landing"] };
        var back = SiteSearchSettings.Parse(settings.ToJson());
        back.Indexable.Should().BeFalse();
        back.Exclude.Should().Equal("/drafts/*");
        back.Include.Should().Equal("/landing");
        back.Excludes("/Drafts/x").Should().BeTrue();
    }

    [Fact]
    public void Only_plain_absolute_paths_are_stored()
    {
        SiteSearchSettings.Validate(new SiteSearchSettings { Exclude = ["/", "/a", "/blog/:slug", "/private/*"], Include = ["/", "/landing"] })
            .Should().BeEmpty();

        SiteSearchSettings.Validate(new SiteSearchSettings
        {
            Exclude = ["", "about", "/a b", "/a/../b", "/a//b", "/a/*/b", "/a*", "/a?x=1"],
            Include = ["/blog/:slug", "/all/*", "/x#y"],
        }).Should().HaveCount(11);

        SiteSearchSettings.Validate(new SiteSearchSettings { Exclude = Enumerable.Range(0, 501).Select(i => $"/p{i}").ToList() })
            .Should().ContainSingle().Which.Should().Contain("500");
    }
}
