extern alias EdgeApp;

using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Dcms.IntegrationTests.Audit;
using Dcms.Shared.Data.Edge;
using Dcms.Shared.Telemetry;
using Dcms.Shared.Vault;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Testcontainers.PostgreSql;
using EdgeCerts = EdgeApp::Dcms.Edge.Certificates;

namespace Dcms.IntegrationTests.Edge;

/// <summary>
/// What "Renew now" does, and what it leaves behind when it cannot finish.
///
/// <para><b>Written after the button did nothing.</b> The reissue request was recorded on the
/// issued certificate, so a managed certificate that had never been issued — which is every one
/// of them until the first order succeeds — had nowhere to put it: admin-api set no flag, the
/// console showed no badge, and the edge's refusal to order without a Cloudflare token was
/// recorded nowhere at all. Three separate silences, one visible symptom: a toast saying it had
/// worked, and nothing else ever changing.</para>
///
/// <para>These tests pin the state transitions rather than the plumbing. Whether NATS delivers
/// the message is not what broke; what broke is what is true in the database afterwards.</para>
/// </summary>
public sealed class ManagedCertificateReissueTests : IAsyncLifetime
{
    private readonly PostgreSqlContainer postgres = TestPostgres.Build();
    private ServiceProvider services = null!;
    private readonly TestMeterFactory meters = new();

    public async ValueTask InitializeAsync()
    {
        await postgres.StartAsync();

        var collection = new ServiceCollection();
        collection.AddSingleton<ITransitEncryptor, PassthroughTransit>();
        collection.AddDbContext<EdgeDbContext>(options =>
            options.UseNpgsql(postgres.GetConnectionString(), npgsql =>
                npgsql.MigrationsHistoryTable("__ef_migrations_history", EdgeDbContext.Schema)));
        services = collection.BuildServiceProvider();

        using var scope = services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<EdgeDbContext>().Database.MigrateAsync();
    }

    public async ValueTask DisposeAsync()
    {
        meters.Dispose();
        await services.DisposeAsync();
        await postgres.DisposeAsync();
    }

    /// <summary>
    /// The case the whole feature exists for and the one that was broken: nothing issued yet, an
    /// operator presses the button, and the edge orders.
    /// </summary>
    [DockerFact]
    public async Task Orders_a_certificate_that_has_never_been_issued()
    {
        var managed = await SeedAsync(reissueRequested: true);
        var acme = new FakeAcme();

        var result = await Provisioner(acme).SweepAsync(TestContext.Current.CancellationToken);

        result.Issued.Should().Be(1);
        acme.Ordered.Should().ContainSingle()
            .Which.Should().BeEquivalentTo(new[] { "highgeek.eu", "*.highgeek.eu" });

        (await ReloadAsync(managed.Id)).ReissueRequestedAt.Should().BeNull(
            "the CA answered, so the request has been dealt with");
    }

    /// <summary>
    /// A healthy certificate nowhere near expiry is not due — that is the point of the renewal
    /// window — so the request is the only thing that can make it due. Without this, pressing the
    /// button on a working certificate is a no-op with a success message.
    /// </summary>
    [DockerFact]
    public async Task Reorders_a_healthy_certificate_that_is_not_yet_due()
    {
        var managed = await SeedAsync(reissueRequested: true);
        await GiveItACertificateAsync(managed);

        var acme = new FakeAcme();
        var result = await Provisioner(acme).SweepAsync(TestContext.Current.CancellationToken);

        result.Renewed.Should().Be(1);
        (await ReloadAsync(managed.Id)).ReissueRequestedAt.Should().BeNull();
    }

    [DockerFact]
    public async Task Leaves_a_healthy_certificate_alone_when_nobody_asked()
    {
        var managed = await SeedAsync(reissueRequested: false);
        await GiveItACertificateAsync(managed);

        var acme = new FakeAcme();
        var result = await Provisioner(acme).SweepAsync(TestContext.Current.CancellationToken);

        result.Should().Be(default(EdgeCerts.ManagedSweepResult));
        acme.Ordered.Should().BeEmpty();
    }

    /// <summary>
    /// The CA said no. The request must be cleared even though it was not satisfied: leaving it
    /// standing would make every hourly sweep re-order, and three of those exhaust the week's
    /// issuances and take TLS off every hostname the wildcard covers.
    /// </summary>
    [DockerFact]
    public async Task Clears_the_request_when_the_ca_refuses()
    {
        var managed = await SeedAsync(reissueRequested: true);
        var acme = new FakeAcme(fail: new InvalidOperationException("DNS problem: NXDOMAIN"));

        var result = await Provisioner(acme).SweepAsync(TestContext.Current.CancellationToken);

        result.Failed.Should().Be(1);
        (await ReloadAsync(managed.Id)).ReissueRequestedAt.Should().BeNull(
            "a standing request plus a refusing CA is a loop that spends the weekly budget");

        var attempts = await AttemptsAsync(managed.Id);
        attempts.Should().ContainSingle();
        attempts[0].Succeeded.Should().BeFalse();
        attempts[0].ReachedCa.Should().BeTrue();
    }

    /// <summary>
    /// The order never happened — no Cloudflare token. Opposite decision to the refusal above,
    /// and for the opposite reason: nothing was spent, so nothing needs protecting, and the fix
    /// is a value in Vault rather than the passage of time. Keeping the request means the
    /// operator's press is honoured on the first sweep after somebody writes the token, instead
    /// of being silently discarded.
    /// </summary>
    [DockerFact]
    public async Task Keeps_the_request_standing_when_the_order_was_never_placed()
    {
        var managed = await SeedAsync(reissueRequested: true);
        var acme = new FakeAcme(fail: new EdgeCerts.CertificateIssuanceUnavailableException(
            "Edge:Dns:Cloudflare:ApiToken is not set, so no DNS-01 challenge can be published."));

        var result = await Provisioner(acme).SweepAsync(TestContext.Current.CancellationToken);

        result.Failed.Should().Be(1);
        (await ReloadAsync(managed.Id)).ReissueRequestedAt.Should().NotBeNull();

        // Recorded but not charged: this is what the console reads to explain itself, and it is
        // the difference between "nothing happened" and "nothing happened, and here is why".
        var attempts = await AttemptsAsync(managed.Id);
        attempts.Should().ContainSingle();
        attempts[0].ReachedCa.Should().BeFalse();
        attempts[0].Error.Should().Contain("ApiToken");
    }

    /// <summary>
    /// A managed failure must not leave a per-hostname placeholder behind.
    ///
    /// <para><c>CertificateStore.RecordFailureAsync</c> files errors against a hostname and
    /// creates a row when there is none — with no key, <c>NotAfter</c> at its default and
    /// <c>Source = DcmsManaged</c>. For a managed certificate that row is indistinguishable from
    /// a per-hostname certificate that has never been issued, so the per-hostname sweep reads it
    /// as due and orders <c>highgeek.eu</c> over HTTP-01 as well: a second certificate for a name
    /// the wildcard already covers, spending the limit this design exists to protect.</para>
    /// </summary>
    [DockerFact]
    public async Task Does_not_leave_a_per_hostname_row_behind_when_it_fails()
    {
        var managed = await SeedAsync(reissueRequested: true);
        var acme = new FakeAcme(fail: new InvalidOperationException("DNS problem: NXDOMAIN"));

        await Provisioner(acme).SweepAsync(TestContext.Current.CancellationToken);

        using var scope = services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<EdgeDbContext>();
        var rows = await db.Certificates.AsNoTracking().ToListAsync(TestContext.Current.CancellationToken);

        rows.Should().BeEmpty("a managed failure belongs in the attempt ledger, not in a hostname row");
        managed.Id.Should().NotBeEmpty();
    }

    private EdgeCerts.ManagedCertificateProvisioner Provisioner(EdgeCerts.IAcmeIssuer acme)
    {
        var store = new EdgeCerts.CertificateStore(
            services, new MemoryCache(new MemoryCacheOptions()), TimeProvider.System,
            NullLogger<EdgeCerts.CertificateStore>.Instance);

        return new EdgeCerts.ManagedCertificateProvisioner(
            services, store, acme,
            Options.Create(new EdgeCerts.CertificateOptions
            {
                TlsEnabled = true,
                ManagedIssuancesPerWeek = 3,
                ManagedFailuresPerHour = 3,
            }),
            new DcmsMetrics(meters), TimeProvider.System,
            NullLogger<EdgeCerts.ManagedCertificateProvisioner>.Instance);
    }

    private async Task<EdgeManagedCertificate> SeedAsync(bool reissueRequested)
    {
        using var scope = services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<EdgeDbContext>();

        var managed = new EdgeManagedCertificate
        {
            Name = "Platform wildcard",
            Identifiers = ["highgeek.eu", "*.highgeek.eu"],
            ReissueRequestedAt = reissueRequested ? DateTimeOffset.UtcNow : null,
        };
        db.ManagedCertificates.Add(managed);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        return managed;
    }

    /// <summary>Gives it a certificate with three months to run, so nothing else makes it due.</summary>
    private async Task GiveItACertificateAsync(EdgeManagedCertificate managed)
    {
        var store = new EdgeCerts.CertificateStore(
            services, new MemoryCache(new MemoryCacheOptions()), TimeProvider.System,
            NullLogger<EdgeCerts.CertificateStore>.Instance);

        var (chainPem, keyPem) = SelfSignedCovering(managed.Identifiers);
        await store.SaveAsync(
            managed.Identifiers[0], chainPem, keyPem, CertificateSource.DcmsManaged, managed.Id,
            TestContext.Current.CancellationToken);
    }

    private async Task<EdgeManagedCertificate> ReloadAsync(Guid id)
    {
        using var scope = services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<EdgeDbContext>();
        return await db.ManagedCertificates.AsNoTracking()
            .FirstAsync(m => m.Id == id, TestContext.Current.CancellationToken);
    }

    private async Task<List<EdgeManagedCertificateAttempt>> AttemptsAsync(Guid id)
    {
        using var scope = services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<EdgeDbContext>();
        return await db.ManagedCertificateAttempts.AsNoTracking()
            .Where(a => a.ManagedCertificateId == id)
            .ToListAsync(TestContext.Current.CancellationToken);
    }

    private static (string ChainPem, string KeyPem) SelfSignedCovering(string[] names)
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest($"CN={names[0]}", key, HashAlgorithmName.SHA256);
        var san = new SubjectAlternativeNameBuilder();
        foreach (var name in names)
        {
            san.AddDnsName(name);
        }

        request.CertificateExtensions.Add(san.Build());
        using var certificate = request.CreateSelfSigned(
            DateTimeOffset.UtcNow.AddDays(-2), DateTimeOffset.UtcNow.AddDays(90));

        return (certificate.ExportCertificatePem(), key.ExportPkcs8PrivateKeyPem());
    }

    /// <summary>An ACME server that issues, or throws whatever this test needs it to throw.</summary>
    private sealed class FakeAcme(Exception? fail = null) : EdgeCerts.IAcmeIssuer
    {
        public List<string[]> Ordered { get; } = [];

        public Task<EdgeCerts.IssuedCertificate> IssueAsync(string hostname, CancellationToken ct)
            => IssueAsync([hostname], ct);

        public Task<EdgeCerts.IssuedCertificate> IssueAsync(
            IReadOnlyList<string> identifiers, CancellationToken ct)
        {
            Ordered.Add([.. identifiers]);
            if (fail is not null)
            {
                throw fail;
            }

            var (chainPem, keyPem) = SelfSignedCovering([.. identifiers]);
            return Task.FromResult(new EdgeCerts.IssuedCertificate(chainPem, keyPem));
        }
    }

    private sealed class PassthroughTransit : ITransitEncryptor
    {
        public Task<string> EncryptAsync(string keyName, ReadOnlyMemory<byte> plaintext, CancellationToken ct = default)
            => Task.FromResult("vault:v1:" + Convert.ToBase64String(plaintext.Span));

        public Task<byte[]> DecryptAsync(string keyName, string ciphertext, CancellationToken ct = default)
            => Task.FromResult(Convert.FromBase64String(ciphertext["vault:v1:".Length..]));
    }
}
