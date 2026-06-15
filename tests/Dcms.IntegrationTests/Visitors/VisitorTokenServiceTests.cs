using Dcms.ContentApi.Visitors;

namespace Dcms.IntegrationTests.Visitors;

/// <summary>Pure-crypto tests for visitor token issuance and tenant isolation — run locally.</summary>
public class VisitorTokenServiceTests
{
    private static VisitorTokenService NewService() =>
        new(new VisitorTokenOptions { SigningKey = "unit-test-signing-key-at-least-32-bytes-long!!" });

    [Fact]
    public async Task Issued_token_validates_for_its_own_tenant()
    {
        var svc = NewService();
        var tenant = Guid.NewGuid();
        var visitor = Guid.NewGuid();

        var token = svc.IssueAccessToken(tenant, visitor, "v@example.com");

        (await svc.ValidateAccessTokenAsync(token, tenant)).Should().Be(visitor);
    }

    [Fact]
    public async Task Token_is_rejected_for_a_different_tenant()
    {
        var svc = NewService();
        var token = svc.IssueAccessToken(Guid.NewGuid(), Guid.NewGuid(), "v@example.com");

        (await svc.ValidateAccessTokenAsync(token, Guid.NewGuid())).Should().BeNull();
    }

    [Fact]
    public async Task Garbage_and_tampered_tokens_are_rejected()
    {
        var svc = NewService();
        var tenant = Guid.NewGuid();
        (await svc.ValidateAccessTokenAsync("not-a-jwt", tenant)).Should().BeNull();

        var token = svc.IssueAccessToken(tenant, Guid.NewGuid(), "v@example.com");
        var tampered = token[..^2] + (token[^1] == 'a' ? "bb" : "aa");
        (await svc.ValidateAccessTokenAsync(tampered, tenant)).Should().BeNull();
    }

    [Fact]
    public void Refresh_token_hash_is_stable_and_matches_the_raw_token()
    {
        var material = NewService().IssueRefreshToken();
        material.TokenHash.Should().Be(VisitorTokenService.HashToken(material.Token));
        material.ExpiresAt.Should().BeAfter(DateTimeOffset.UtcNow);
    }
}
