extern alias AdminApiApp;

using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using AdminUpload = AdminApiApp::Dcms.AdminApi.Tenancy.CertificateUpload;

namespace Dcms.IntegrationTests.Edge;

/// <summary>
/// The rules an uploaded certificate has to pass before the edge will serve it.
///
/// <para>Each one exists to move a failure out of the TLS handshake. A certificate that does not
/// match its key, or does not cover the hostname, fails on the tenant's live domain with a
/// browser warning as the only symptom and a closed connection as the only explanation. These
/// turn every one of those into a sentence at upload time.</para>
/// </summary>
public class CertificateUploadTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 4, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Accepts_a_matching_pair_that_covers_the_hostname()
    {
        var (chain, key) = SelfSigned("shop.tenant.example");

        AdminUpload.Validate(new AdminUpload.Request(chain, key), "shop.tenant.example", Now)
            .Should().BeNull();
    }

    [Fact]
    public void Refuses_a_key_that_belongs_to_a_different_certificate()
    {
        var (chain, _) = SelfSigned("shop.tenant.example");
        var (_, otherKey) = SelfSigned("shop.tenant.example");

        // The single most common upload mistake, and the one whose runtime symptom is least
        // legible: a handshake that fails with no server-side explanation at all.
        AdminUpload.Validate(new AdminUpload.Request(chain, otherKey), "shop.tenant.example", Now)
            .Should().Contain("does not match");
    }

    [Fact]
    public void Refuses_a_certificate_for_somebody_elses_hostname()
    {
        var (chain, key) = SelfSigned("other.example");

        AdminUpload.Validate(new AdminUpload.Request(chain, key), "shop.tenant.example", Now)
            .Should().Contain("does not cover");
    }

    [Fact]
    public void Refuses_one_that_has_already_expired()
    {
        var (chain, key) = SelfSigned("shop.tenant.example", notAfter: Now.AddDays(-1));

        AdminUpload.Validate(new AdminUpload.Request(chain, key), "shop.tenant.example", Now)
            .Should().Contain("expired");
    }

    [Fact]
    public void Refuses_one_that_is_not_valid_yet()
    {
        var (chain, key) = SelfSigned(
            "shop.tenant.example", notBefore: Now.AddDays(10), notAfter: Now.AddDays(100));

        AdminUpload.Validate(new AdminUpload.Request(chain, key), "shop.tenant.example", Now)
            .Should().Contain("not valid until");
    }

    [Fact]
    public void Refuses_something_that_is_not_a_certificate_at_all()
    {
        AdminUpload.Validate(new AdminUpload.Request("hello", "world"), "shop.tenant.example", Now)
            .Should().NotBeNull();
    }

    [Theory]
    // A wildcard covers exactly one label, and only the leftmost -- which is what a browser
    // does. Accepting more here would install a certificate clients then reject, and the
    // rejection happens on the tenant's domain rather than on this form.
    [InlineData("*.tenant.example", "shop.tenant.example", true)]
    [InlineData("*.tenant.example", "a.b.tenant.example", false)]
    [InlineData("*.tenant.example", "tenant.example", false)]
    [InlineData("shop.tenant.example", "SHOP.TENANT.EXAMPLE", true)]
    [InlineData("shop.tenant.example", "shop.tenant.example.evil.test", false)]
    public void Matches_names_the_way_a_client_does(string sanName, string hostname, bool covered)
    {
        var (chain, _) = SelfSigned(sanName);
        using var leaf = AdminUpload.ParseLeaf(chain)!;

        AdminUpload.CoversHostname(leaf, hostname).Should().Be(covered);
    }

    /// <summary>
    /// The common name is deliberately something else in every certificate here: it has not been
    /// authoritative for over a decade, and a validator that fell back to it would accept
    /// certificates no browser does.
    /// </summary>
    [Fact]
    public void Never_falls_back_to_the_common_name()
    {
        var (chain, key) = SelfSigned("other.example", commonName: "shop.tenant.example");

        AdminUpload.Validate(new AdminUpload.Request(chain, key), "shop.tenant.example", Now)
            .Should().Contain("does not cover");
    }

    private static (string ChainPem, string KeyPem) SelfSigned(
        string sanName,
        string? commonName = null,
        DateTimeOffset? notBefore = null,
        DateTimeOffset? notAfter = null)
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest($"CN={commonName ?? sanName}", key, HashAlgorithmName.SHA256);
        var san = new SubjectAlternativeNameBuilder();
        san.AddDnsName(sanName);
        request.CertificateExtensions.Add(san.Build());

        using var certificate = request.CreateSelfSigned(
            notBefore ?? Now.AddDays(-2), notAfter ?? Now.AddDays(90));

        return (certificate.ExportCertificatePem(), key.ExportPkcs8PrivateKeyPem());
    }
}
