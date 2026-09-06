using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Dcms.Edge.Certificates;
using Dcms.Edge.Certificates.Dns;

namespace Dcms.UnitTests.Edge;

/// <summary>
/// The pure parts of wildcard handling: which wildcard could cover a hostname, where a DNS-01
/// challenge record belongs, and how an identifier set is keyed for the rate-limit ledger.
///
/// <para>Small functions, but each of them is a place a plausible mistake is invisible until it
/// costs a week of issuance. See <c>docs/adr/0011-wildcard-tls-dns01.md</c>.</para>
/// </summary>
public class WildcardCertificateTests
{
    [Theory]
    [InlineData("a.dcms.highgeek.eu", "*.dcms.highgeek.eu")]
    [InlineData("admin.highgeek.eu", "*.highgeek.eu")]
    // One level only, per RFC 6125. This is the case that makes *.highgeek.eu insufficient on
    // its own and forces *.dcms.highgeek.eu to be a second identifier.
    [InlineData("x.dcms.highgeek.eu", "*.dcms.highgeek.eu")]
    [InlineData("highgeek.eu", "*.eu")]
    public void Names_the_one_wildcard_that_could_cover_a_hostname(string hostname, string expected)
        => CertificateStore.WildcardParent(hostname).Should().Be(expected);

    [Theory]
    [InlineData("localhost")]
    [InlineData("")]
    [InlineData("trailing.")]
    public void Has_no_wildcard_parent_for_a_name_with_nothing_to_its_left(string hostname)
        => CertificateStore.WildcardParent(hostname).Should().BeNull();

    /// <summary>
    /// The collision that makes DNS-01 subtle: an apex and its wildcard publish at the SAME
    /// record name, so an implementation that replaces rather than adds satisfies one
    /// authorization and fails the other.
    /// </summary>
    [Fact]
    public void Puts_an_apex_and_its_wildcard_challenge_at_the_same_record_name()
    {
        DnsChallenge.RecordName("highgeek.eu").Should().Be("_acme-challenge.highgeek.eu");
        DnsChallenge.RecordName("*.highgeek.eu").Should().Be("_acme-challenge.highgeek.eu");
    }

    [Fact]
    public void Strips_only_the_wildcard_label_when_naming_the_challenge()
        => DnsChallenge.RecordName("*.dcms.highgeek.eu")
            .Should().Be("_acme-challenge.dcms.highgeek.eu");

    [Theory]
    [InlineData("*.highgeek.eu", true)]
    [InlineData("highgeek.eu", false)]
    // Not a wildcard identifier: ACME only recognises a bare "*" as the whole leftmost label.
    [InlineData("*a.highgeek.eu", false)]
    public void Recognises_which_identifiers_force_dns_validation(string identifier, bool expected)
        => DnsChallenge.IsWildcard(identifier).Should().Be(expected);

    /// <summary>
    /// The CA counts its duplicate-certificate limit against the SET of identifiers. If the key
    /// depended on their order, reordering them in the console would look like a fresh weekly
    /// budget — a way to spend the real limit several times over while the guard reported that
    /// nothing had been spent.
    /// </summary>
    [Fact]
    public void Keys_an_identifier_set_independently_of_the_order_it_was_written_in()
    {
        var a = ManagedCertificateGuard.IdentifierKey(["highgeek.eu", "*.highgeek.eu", "*.dcms.highgeek.eu"]);
        var b = ManagedCertificateGuard.IdentifierKey(["*.dcms.highgeek.eu", "highgeek.eu", "*.highgeek.eu"]);

        a.Should().Be(b);
    }

    [Fact]
    public void Treats_a_different_set_of_identifiers_as_a_different_budget()
    {
        var a = ManagedCertificateGuard.IdentifierKey(["highgeek.eu", "*.highgeek.eu"]);
        var b = ManagedCertificateGuard.IdentifierKey(["highgeek.eu"]);

        a.Should().NotBe(b);
    }

    [Fact]
    public void Normalises_case_and_duplicates_when_keying_a_set()
        => ManagedCertificateGuard.IdentifierKey(["HighGeek.EU", "highgeek.eu", "*.HIGHGEEK.eu"])
            .Should().Be("*.highgeek.eu highgeek.eu");

    /// <summary>
    /// The stored coverage list is read off the certificate rather than copied from what was
    /// ordered, so it cannot claim a name the leaf does not actually carry.
    /// </summary>
    [Fact]
    public void Reads_every_covered_name_off_the_issued_certificate()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest("CN=highgeek.eu", key, HashAlgorithmName.SHA256);
        var san = new SubjectAlternativeNameBuilder();
        san.AddDnsName("highgeek.eu");
        san.AddDnsName("*.highgeek.eu");
        san.AddDnsName("*.dcms.highgeek.eu");
        request.CertificateExtensions.Add(san.Build());

        using var certificate = request.CreateSelfSigned(
            DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(90));

        CertificateStore.ReadSubjectAlternativeNames(certificate)
            .Should().BeEquivalentTo(["highgeek.eu", "*.highgeek.eu", "*.dcms.highgeek.eu"]);
    }

    [Fact]
    public void Reports_no_coverage_for_a_certificate_that_carries_no_san_extension()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest("CN=legacy.example", key, HashAlgorithmName.SHA256);
        using var certificate = request.CreateSelfSigned(
            DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(90));

        CertificateStore.ReadSubjectAlternativeNames(certificate).Should().BeEmpty();
    }
}
