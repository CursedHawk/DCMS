using Dcms.Edge.Auth;

namespace Dcms.UnitTests.Edge;

/// <summary>
/// What the edge will and will not accept as a session's token pair (ADR 0014).
///
/// <para>One factory serves both entry points — the sign-in path has a typed
/// <c>OpenIdConnectMessage</c> and the refresh path has raw JSON — so the rules cannot drift
/// apart between them. Each case below is a response shape that would otherwise produce a
/// session that fails later, somewhere else, for a reason nobody can trace back to here.</para>
/// </summary>
public class BffTokenTests
{
    [Fact]
    public void Builds_a_session_from_a_normal_token_response()
    {
        var tokens = BffTokenProvider.Create("access-1", "refresh-1", "600", previousRefreshToken: null);

        tokens.Should().NotBeNull();
        tokens!.AccessToken.Should().Be("access-1");
        tokens.RefreshToken.Should().Be("refresh-1");
        tokens.ExpiresAt.Should().BeCloseTo(DateTimeOffset.UtcNow.AddMinutes(10), TimeSpan.FromSeconds(5));
    }

    [Fact]
    public void Keeps_the_previous_refresh_token_when_the_server_does_not_rotate()
    {
        // OpenIddict rotates by default, but that is a server setting. Assuming rotation and
        // dropping the token we still hold would end every session at its first refresh on a
        // server configured the other way.
        var tokens = BffTokenProvider.Create("access-2", refreshToken: null, "600", previousRefreshToken: "refresh-1");

        tokens!.RefreshToken.Should().Be("refresh-1");
    }

    [Fact]
    public void Takes_the_rotated_refresh_token_when_there_is_one()
    {
        var tokens = BffTokenProvider.Create("access-2", "refresh-2", "600", previousRefreshToken: "refresh-1");

        // Keeping the old one here is the bug that ends a session on the *second* refresh,
        // hours later, with nothing in the log tying it to the first.
        tokens!.RefreshToken.Should().Be("refresh-2");
    }

    [Fact]
    public void Refuses_a_sign_in_with_no_refresh_token_at_all()
    {
        // Without one the session dies silently when the 10-minute access token expires. A
        // refused sign-in lands on the sign-in page, which is a failure somebody can read.
        BffTokenProvider.Create("access-1", refreshToken: null, "600", previousRefreshToken: null)
            .Should().BeNull();
    }

    [Fact]
    public void Refuses_a_response_with_no_access_token()
    {
        BffTokenProvider.Create(accessToken: null, "refresh-1", "600", previousRefreshToken: null)
            .Should().BeNull();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not-a-number")]
    [InlineData("0")]
    [InlineData("-60")]
    public void Falls_back_to_the_known_access_token_lifetime_when_expires_in_is_unusable(string? expiresIn)
    {
        // A zero or negative lifetime would mark the token expired on arrival and send every
        // request into the refresh path — a working session that refreshes on every call.
        var tokens = BffTokenProvider.Create("access-1", "refresh-1", expiresIn, previousRefreshToken: null);

        tokens!.ExpiresAt.Should().BeCloseTo(DateTimeOffset.UtcNow.AddMinutes(10), TimeSpan.FromSeconds(5));
    }

    [Fact]
    public void Reads_the_session_id_off_a_principal_and_nothing_off_one_without()
    {
        var identity = new System.Security.Claims.ClaimsIdentity(
            [new System.Security.Claims.Claim(BffSessionStore.SessionIdClaim, "sid-1")], "test");

        BffTokenProvider.SessionIdOf(new System.Security.Claims.ClaimsPrincipal(identity)).Should().Be("sid-1");
        // A Grafana or Forgejo session has no session id, and must not be mistaken for one that
        // can call admin-api.
        BffTokenProvider.SessionIdOf(new System.Security.Claims.ClaimsPrincipal()).Should().BeNull();
        BffTokenProvider.SessionIdOf(null).Should().BeNull();
    }
}
