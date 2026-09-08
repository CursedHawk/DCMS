using Dcms.Edge.Transforms;

namespace Dcms.UnitTests.Edge;

/// <summary>
/// The cookies that put Grafana in a permanent reload loop, and the edge's disposal of them.
///
/// <para>The loop: <c>grafana_session_expiry</c> is script-readable, and a timestamp in the past
/// makes Grafana's frontend POST a token rotation at once. The 401 it gets makes it reload. Under
/// <c>auth.proxy</c> the reloaded page is signed in again, so it rotates again — about once a
/// second, with no exit, because the frontend's recovery from a dead session is the one thing
/// that always succeeds here.</para>
/// </summary>
public class GrafanaSessionCookieTests
{
    [Fact]
    public void Notices_the_cookie_that_starts_the_loop()
    {
        GrafanaSessionCookies.Present("grafana_session_expiry=1757370000").Should().BeTrue();
        GrafanaSessionCookies.Present("grafana_session=abc123").Should().BeTrue();
    }

    [Fact]
    public void Leaves_a_browser_that_has_neither_alone()
    {
        // The response transform is keyed on this: a healthy browser must not collect two
        // pointless Set-Cookie headers on every dashboard request.
        GrafanaSessionCookies.Present("dcms.edge=xyz; theme=dark").Should().BeFalse();
        GrafanaSessionCookies.Present("").Should().BeFalse();
        GrafanaSessionCookies.Present(null).Should().BeFalse();
    }

    [Fact]
    public void Forwards_every_other_cookie_untouched()
    {
        // Grafana reads its own preferences from cookies, and the edge's session rides on this
        // route too. Dropping the whole header would be a bigger outage than the loop.
        GrafanaSessionCookies.Without("dcms.edge=xyz; grafana_session=abc; theme=dark")
            .Should().Be("dcms.edge=xyz; theme=dark");
    }

    [Fact]
    public void Sends_no_cookie_header_at_all_when_nothing_survives()
    {
        // Null rather than "", so the request goes out without the header instead of with an
        // empty one -- which some servers parse as a single nameless cookie.
        GrafanaSessionCookies.Without("grafana_session=abc; grafana_session_expiry=1757370000")
            .Should().BeNull();
    }

    [Fact]
    public void Is_not_fooled_by_a_name_that_merely_contains_one()
    {
        var kept = GrafanaSessionCookies.Without("my_grafana_session=abc; grafana_session_id=z");
        kept.Should().Be("my_grafana_session=abc; grafana_session_id=z");
    }

    [Fact]
    public void Deletes_at_the_path_the_browser_will_match()
    {
        // A deletion matches on name, domain and path. At the wrong path the original stays put
        // and a second, empty cookie appears beside it -- so the loop would continue and the
        // evidence would look like the fix had been applied.
        var deletions = GrafanaSessionCookies.Deletions().ToArray();

        deletions.Should().HaveCount(2);
        deletions.Should().OnlyContain(value => value.Contains("Path=/"));
        deletions.Should().OnlyContain(value => value.Contains("Max-Age=0"));
        deletions.Should().Contain(value => value.StartsWith("grafana_session="));
        deletions.Should().Contain(value => value.StartsWith("grafana_session_expiry="));
    }
}
