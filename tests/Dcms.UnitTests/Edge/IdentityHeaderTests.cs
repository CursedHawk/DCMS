using System.Security.Claims;
using Dcms.Edge.Transforms;
using Microsoft.AspNetCore.Http;

namespace Dcms.UnitTests.Edge;

/// <summary>
/// Who the edge vouches for, and — more importantly — when it declines to.
///
/// <para><c>X-WEBAUTH-USER</c> is a bearer credential in header form: whoever can set it on a
/// request that reaches Grafana or Forgejo <i>is</i> that user. Every case below is one where
/// asserting an identity would be worse than asserting none.</para>
/// </summary>
public class IdentityHeaderTests
{
    [Fact]
    public void Tells_Grafana_the_email_it_keys_accounts_on()
    {
        var headers = Resolve(IdentityHeaders.Grafana, SignedIn(
            ("email", "ops@highgeek.eu"), ("name", "Ops"), ("role", "SuperAdmin")));

        headers["X-WEBAUTH-USER"].Should().Be("ops@highgeek.eu");
        headers["X-WEBAUTH-EMAIL"].Should().Be("ops@highgeek.eu");
        headers["X-WEBAUTH-NAME"].Should().Be("Ops");
        // Sent explicitly. Without it Grafana falls back to auto_assign_org_role (Viewer), and
        // an operator who got past the SuperAdmin gate lands unable to edit anything.
        headers["X-WEBAUTH-ROLE"].Should().Be("Admin");
    }

    [Fact]
    public void Tells_Forgejo_the_name_identity_allocated_and_never_one_it_guessed()
    {
        // Forgejo usernames come from ForgejoUserSync with a numeric suffix on collision, so
        // "rgolias" and "rgolias-2" are two different people with similar addresses. Recomputing
        // this from the email would sign one of them in as the other, in a server holding every
        // tenant's site repositories.
        var headers = Resolve(IdentityHeaders.Forgejo, SignedIn(
            ("email", "rgolias@example.com"), ("forgejo_username", "rgolias-2")));

        headers["X-WEBAUTH-USER"].Should().Be("rgolias-2");
    }

    [Fact]
    public void Says_nothing_to_Forgejo_about_a_user_with_no_mirrored_account()
    {
        var headers = Resolve(IdentityHeaders.Forgejo, SignedIn(("email", "new@example.com")));

        // Forgejo's own sign-in page is the right answer here. Inventing a name would claim one
        // the sync is about to allocate to somebody else -- which is why auto-registration is
        // off on that side too.
        headers.Should().BeEmpty();
    }

    [Fact]
    public void Never_overrides_a_credential_the_caller_presented()
    {
        var context = SignedIn(("email", "ops@highgeek.eu"), ("forgejo_username", "ops"));
        context.Request.Headers.Authorization = "Basic Zm9vOmJhcg==";

        // This is the second guard on git-over-HTTP, after the route split. A `git push` carries
        // the per-user Basic password the sync provisioned; replacing the identity it proves
        // with a browser session's would attribute the push to whoever is signed in in that
        // browser and check permissions against the wrong account.
        Resolve(IdentityHeaders.Forgejo, context).Should().BeEmpty();
        Resolve(IdentityHeaders.Grafana, context).Should().BeEmpty();
    }

    [Fact]
    public void Says_nothing_for_an_anonymous_caller()
    {
        var anonymous = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity()) };

        Resolve(IdentityHeaders.Grafana, anonymous).Should().BeEmpty();
        Resolve(IdentityHeaders.Forgejo, anonymous).Should().BeEmpty();
    }

    [Fact]
    public void Says_nothing_to_Grafana_without_an_email_to_say()
    {
        // Grafana keys its accounts on this value and auto-creates. A blank one would make an
        // account nobody owns rather than fail a sign-in.
        Resolve(IdentityHeaders.Grafana, SignedIn(("name", "Nameless"))).Should().BeEmpty();
    }

    private static Dictionary<string, string> Resolve(string dialect, HttpContext context)
        => IdentityHeaders.Resolve(dialect, context).ToDictionary(h => h.Key, h => h.Value);

    private static DefaultHttpContext SignedIn(params (string Type, string Value)[] claims)
        => new()
        {
            User = new ClaimsPrincipal(new ClaimsIdentity(
                claims.Select(c => new Claim(c.Type, c.Value)), "edge-test", "email", "role")),
        };
}
