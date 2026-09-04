using Dcms.Edge.Certificates;
using Dcms.Shared.Data.Edge;
using Dcms.Shared.Telemetry;
using Dcms.UnitTests.Audit;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace Dcms.UnitTests.Edge;

/// <summary>
/// The provisioner is the only path to an ACME order, and the guards it applies are what stand
/// between this platform and a Let's Encrypt rate-limit block — a platform-wide failure that
/// lasts a week and cannot be hurried. Each guard is asserted here rather than trusted.
/// </summary>
public class CertificateProvisionerTests
{
    [Fact]
    public async Task Never_contacts_the_CA_for_a_hostname_that_is_not_ours()
    {
        var issuer = Substitute.For<IAcmeIssuer>();
        var provisioner = Build(issuer, allowed: false, out _);

        var result = await provisioner.EnsureAsync("someone-elses-domain.example", TestContext.Current.CancellationToken);

        result.Should().BeNull();
        // The important half is not "returned nothing" but "never spoke to the CA". A public IP
        // attracts hostnames strangers have pointed at it, and issuing for them would spend our
        // account's budget on their domains.
        await issuer.DidNotReceive().IssueAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Stores_what_the_CA_issued()
    {
        var issuer = Substitute.For<IAcmeIssuer>();
        issuer.IssueAsync("shop.tenant.example", Arg.Any<CancellationToken>())
            .Returns(new IssuedCertificate("chain-pem", "key-pem"));
        var provisioner = Build(issuer, allowed: true, out var store);

        await provisioner.EnsureAsync("shop.tenant.example", TestContext.Current.CancellationToken);

        await store.Received(1).SaveAsync(
            "shop.tenant.example", "chain-pem", "key-pem", CertificateSource.DcmsManaged, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Records_the_CAs_explanation_instead_of_throwing()
    {
        var issuer = Substitute.For<IAcmeIssuer>();
        issuer.IssueAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new AcmeIssuanceException("DNS problem: NXDOMAIN looking up A for broken.tenant.example"));
        var provisioner = Build(issuer, allowed: true, out var store);

        var result = await provisioner.EnsureAsync("broken.tenant.example", TestContext.Current.CancellationToken);

        result.Should().BeNull();
        // Kept because it is the sentence that tells whoever configured the DNS what to fix.
        // A file-based edge had nowhere to put it: it logged the error and moved on.
        await store.Received(1).RecordFailureAsync(
            "broken.tenant.example", Arg.Is<string>(e => e.Contains("NXDOMAIN")), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Backs_off_after_repeated_failures()
    {
        var issuer = Substitute.For<IAcmeIssuer>();
        var provisioner = Build(issuer, allowed: true, out var store);
        store.RetryNotBeforeAsync(Arg.Any<string>(), Arg.Any<CertificateOptions>(), Arg.Any<CancellationToken>())
            .Returns(DateTimeOffset.UtcNow.AddMinutes(10));

        var result = await provisioner.EnsureAsync("broken.tenant.example", TestContext.Current.CancellationToken);

        result.Should().BeNull();
        // Failed authorizations are rate-limited as well as successful issuance, so one domain
        // retrying in a loop can exhaust every other tenant's budget.
        await issuer.DidNotReceive().IssueAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Opens_one_order_for_concurrent_requests_for_the_same_hostname()
    {
        var gate = new TaskCompletionSource();
        var calls = 0;
        var issuer = Substitute.For<IAcmeIssuer>();
        issuer.IssueAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(async _ =>
            {
                Interlocked.Increment(ref calls);
                await gate.Task;
                return new IssuedCertificate("chain-pem", "key-pem");
            });
        var provisioner = Build(issuer, allowed: true, out _);

        var requests = Enumerable.Range(0, 8)
            .Select(_ => provisioner.EnsureAsync("busy.tenant.example", TestContext.Current.CancellationToken))
            .ToArray();
        gate.SetResult();
        await Task.WhenAll(requests);

        // A popular domain whose certificate has just expired attracts many simultaneous
        // handshakes. Without single-flight each would open its own order for the same name.
        calls.Should().Be(1);
    }

    /// <summary>Hostnames are matched case-insensitively and without a port, as SNI supplies them.</summary>
    [Theory]
    [InlineData("Shop.Tenant.Example")]
    [InlineData("shop.tenant.example:443")]
    [InlineData("shop.tenant.example.")]
    public async Task Normalises_the_hostname_before_anything_else_sees_it(string input)
    {
        var issuer = Substitute.For<IAcmeIssuer>();
        issuer.IssueAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new IssuedCertificate("chain-pem", "key-pem"));
        var provisioner = Build(issuer, allowed: true, out _);

        await provisioner.EnsureAsync(input, TestContext.Current.CancellationToken);

        await issuer.Received(1).IssueAsync("shop.tenant.example", Arg.Any<CancellationToken>());
    }

    private static CertificateProvisioner Build(IAcmeIssuer issuer, bool allowed, out ICertificateStore store)
    {
        store = Substitute.For<ICertificateStore>();
        var allowList = Substitute.For<ITlsAllowList>();
        allowList.IsAllowedAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(allowed);
        return new CertificateProvisioner(
            store, issuer, allowList,
            new DcmsMetrics(new TestMeterFactory()),
            Options.Create(new CertificateOptions()),
            NullLogger<CertificateProvisioner>.Instance);
    }
}
