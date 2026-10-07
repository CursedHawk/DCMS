using Dcms.Edge.Auth;

namespace Dcms.UnitTests.Edge;

/// <summary>
/// A tenant site's access rules at the edge (ADR 0022): which rule decides a path — including
/// every spelling a client could use to slip past one — and what the gate does about it.
/// </summary>
public class SiteGateTests
{
    private static readonly Guid Staff = Guid.NewGuid();

    private static readonly SiteGateEntry Site = new(Guid.NewGuid(), "corp",
    [
        new SiteRule("/portal/public", SiteAccess.Public),
        new SiteRule("/portal", SiteAccess.Groups, [Staff]),
        new SiteRule("/members", SiteAccess.SignedIn),
    ]);

    private static SiteSession Session(params Guid[] groups) =>
        new(Site.TenantId, "u", "Pat", "pat@corp.test", groups, "access", "refresh", DateTimeOffset.UtcNow.AddMinutes(5));

    [Theory]
    [InlineData("/portal")]
    [InlineData("/portal/")]
    [InlineData("/portal/reports")]
    [InlineData("/Portal/reports")]
    [InlineData("/PORTAL")]
    [InlineData("//portal/reports")]
    [InlineData("/./portal/reports")]
    [InlineData("/x/../portal/reports")]
    [InlineData("/members/../portal")]
    // site-host's flat artifact names for the same pages.
    [InlineData("/portal.html")]
    [InlineData("/portal_reports.html")]
    [InlineData("/PORTAL_Reports.HTML")]
    [InlineData("/portal_@.html")]
    [InlineData("/_portal.html")]
    [InlineData("/portal__reports.html")]
    public void Every_spelling_of_a_gated_path_meets_its_rule(string path) =>
        SiteGateRules.Decide(Site.Rules, path)!.Prefix.Should().Be("/portal");

    // Kestrel has already decoded these once; what is left is the second layer of a double
    // encoding, an encoded slash it leaves alone, or a backslash. Each could name one file to the
    // edge and another to the server behind, so the gate refuses them rather than decoding again.
    [Theory]
    [InlineData("/%70ortal/reports")]
    [InlineData("/portal%2Freports")]
    [InlineData("/public%2F..%2Fportal")]
    [InlineData("/.edge/..%2Fportal")]
    [InlineData("\\portal\\reports")]
    public void Paths_the_edge_and_the_server_could_read_differently_are_refused(string path) =>
        SiteGateRules.IsAmbiguous(path).Should().BeTrue();

    [Fact]
    public void Ordinary_paths_are_not_ambiguous() =>
        SiteGateRules.IsAmbiguous("/portal/reports/2026 q3.pdf").Should().BeFalse();

    [Theory]
    [InlineData("/")]
    [InlineData("/portals")]
    [InlineData("/about")]
    [InlineData("/portal/public")]
    [InlineData("/portal/public/brochure.pdf")]
    [InlineData("/portal_public.html")]
    [InlineData("/portals.html")]
    [InlineData("/about.html")]
    public void Paths_no_rule_covers_or_a_public_rule_covers_are_open(string path) =>
        SiteGateRules.Decide(Site.Rules, path).Should().BeNull();

    [Fact]
    public void The_first_matching_rule_decides()
    {
        // /portal/public sits above /portal, so it wins for its own paths only.
        SiteGateRules.Decide(Site.Rules, "/portal/public/x").Should().BeNull();
        SiteGateRules.Decide(Site.Rules, "/portal/private").Should().NotBeNull();
    }

    [Theory]
    [InlineData(true, SiteGateOutcome.Challenge)]
    [InlineData(false, SiteGateOutcome.Unauthorized)]
    public void Without_a_session_a_page_load_signs_in_and_an_api_call_is_refused(bool pageLoad, SiteGateOutcome expected) =>
        SiteAuthentication.Decide(Site, "/members", session: null, sitesEnabled: true, pageLoad).Should().Be(expected);

    [Fact]
    public void Rules_the_edge_cannot_enforce_close_the_gated_paths_rather_than_open_them()
    {
        SiteAuthentication.Decide(Site, "/members", null, sitesEnabled: false, pageLoad: true).Should().Be(SiteGateOutcome.Unavailable);
        SiteAuthentication.Decide(Site, "/about", null, sitesEnabled: false, pageLoad: true).Should().Be(SiteGateOutcome.Pass);
    }

    [Fact]
    public void Groups_rules_admit_members_only()
    {
        SiteAuthentication.Decide(Site, "/portal", Session(Staff), true, true).Should().Be(SiteGateOutcome.Pass);
        SiteAuthentication.Decide(Site, "/portal", Session(Guid.NewGuid()), true, true).Should().Be(SiteGateOutcome.Forbidden);
        SiteAuthentication.Decide(Site, "/members", Session(), true, true).Should().Be(SiteGateOutcome.Pass, "any signed-in user");
    }

    [Fact]
    public void Each_realm_client_has_its_own_secret()
    {
        var a = SiteAuthentication.SecretFor("master", "site:aaaa");
        a.Should().Be(SiteAuthentication.SecretFor("master", "site:aaaa"), "identity derives the same one");
        a.Should().NotBe(SiteAuthentication.SecretFor("master", "site:bbbb"));
        a.Should().NotBe(SiteAuthentication.SecretFor("other", "site:aaaa"));
    }

    [Fact]
    public void A_trailing_dot_names_the_same_host()
    {
        var gates = new SiteGates();
        gates.Replace(new Dictionary<string, SiteGateEntry>(StringComparer.OrdinalIgnoreCase) { ["corp.example"] = Site });
        gates.For("corp.example.").Should().BeSameAs(Site);
        gates.For("CORP.Example").Should().BeSameAs(Site);
    }

    [Fact]
    public void Nothing_is_known_until_the_first_load() =>
        new SiteGates().Loaded.Should().BeFalse();
}
