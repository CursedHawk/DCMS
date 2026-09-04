extern alias EdgeApp;

using System.Diagnostics.Metrics;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Dcms.Shared.Data.Edge;
using Dcms.Shared.Telemetry;
using Dcms.Shared.Vault;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Testcontainers.PostgreSql;
using EdgeCerts = EdgeApp::Dcms.Edge.Certificates;

namespace Dcms.IntegrationTests.Edge;

/// <summary>
/// The certificate sweep, against a real database and a fake CA.
///
/// <para><b>This is the test that stands between the platform and a total outage.</b> There is
/// no second ingress: if the edge holds no certificate for a hostname it aborts the TLS
/// handshake, and a browser reports <c>ERR_CONNECTION_CLOSED</c> — no page, no status code,
/// nothing to read. So "the store is empty" has to be a state the platform corrects on its
/// own, and the sweep is the only thing that corrects it.</para>
///
/// <para>On-demand issuance cannot: it happens inside a visitor's handshake, bounded by
/// <c>OnDemandTimeoutSeconds</c>, and a full ACME order usually takes longer. The first visit
/// fails, records a failure, backs off, and the next visit fails further away. Nothing
/// recovers.</para>
/// </summary>
public sealed class CertificateSweepTests : IAsyncLifetime
{
    private readonly PostgreSqlContainer postgres = TestPostgres.Build();
    private ServiceProvider services = null!;
    private TestMeterFactory meters = null!;

    public async ValueTask InitializeAsync()
    {
        await postgres.StartAsync();

        var collection = new ServiceCollection();
        collection.AddSingleton<ITransitEncryptor, PassthroughTransit>();
        collection.AddDbContext<EdgeDbContext>(options =>
            options.UseNpgsql(postgres.GetConnectionString(), npgsql =>
                npgsql.MigrationsHistoryTable("__ef_migrations_history", EdgeDbContext.Schema)));
        services = collection.BuildServiceProvider();
        meters = new TestMeterFactory();

        using var scope = services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<EdgeDbContext>().Database.MigrateAsync();
    }

    public async ValueTask DisposeAsync()
    {
        meters.Dispose();
        await services.DisposeAsync();
        await postgres.DisposeAsync();
    }

    [DockerFact]
    public async Task Issues_every_allowed_hostname_it_holds_no_certificate_for()
    {
        // The cold-start case, and the one the platform actually hit: an empty edge.certificates
        // with five platform hostnames and a tenant domain all pointed at the box. Every one of
        // them must come back from a single sweep, without a visitor having to trigger it.
        var issuer = new FakeAcme();
        var service = BuildSweep(issuer, allowed: [
            "admin.highgeek.eu", "platform.highgeek.eu", "auth.highgeek.eu",
            "grafana.highgeek.eu", "git.highgeek.eu", "shop.tenant.example",
        ]);

        await RunOneSweepAsync(service, until: () => issuer.Ordered.Count >= 6);

        issuer.Ordered.Should().BeEquivalentTo([
            "admin.highgeek.eu", "platform.highgeek.eu", "auth.highgeek.eu",
            "grafana.highgeek.eu", "git.highgeek.eu", "shop.tenant.example",
        ]);

        using var scope = services.CreateScope();
        var stored = await scope.ServiceProvider.GetRequiredService<EdgeDbContext>()
            .Certificates.AsNoTracking().Select(c => c.Hostname)
            .ToListAsync(TestContext.Current.CancellationToken);
        stored.Should().HaveCount(6);
    }

    [DockerFact]
    public async Task Does_not_reorder_a_hostname_it_already_holds()
    {
        // Let's Encrypt allows ~50 certificates per registered domain per week, and every
        // managed subdomain shares one registered domain. A sweep that reordered what it
        // already had would exhaust that budget within a day and then fail for everyone --
        // including the renewals, which is how a rate limit turns into an outage.
        await SeedAsync("admin.highgeek.eu");

        var issuer = new FakeAcme();
        var service = BuildSweep(issuer, allowed: ["admin.highgeek.eu", "new.tenant.example"]);

        await RunOneSweepAsync(service, until: () => issuer.Ordered.Count >= 1);

        issuer.Ordered.Should().ContainSingle().Which.Should().Be("new.tenant.example");
    }

    [DockerFact]
    public async Task Records_the_failure_and_keeps_going_when_one_hostname_cannot_be_issued()
    {
        // One tenant whose DNS record was removed after verification must not stop the sweep:
        // the hostnames behind it in the list include the operator plane, and an edge that gave
        // up on the first NXDOMAIN would leave the console unreachable because of somebody
        // else's domain.
        var issuer = new FakeAcme(failFor: "broken.tenant.example");
        var service = BuildSweep(issuer, allowed: ["broken.tenant.example", "admin.highgeek.eu"]);

        await RunOneSweepAsync(service, until: () => issuer.Ordered.Count >= 2);

        using var scope = services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<EdgeDbContext>();

        (await db.Certificates.AsNoTracking()
                .AnyAsync(c => c.Hostname == "admin.highgeek.eu", TestContext.Current.CancellationToken))
            .Should().BeTrue();

        // The CA's own sentence, kept rather than logged once and discarded -- it is what the
        // admin UI shows next to the domain, and usually it names the actual problem.
        var broken = await db.Certificates.AsNoTracking()
            .FirstAsync(c => c.Hostname == "broken.tenant.example", TestContext.Current.CancellationToken);
        broken.ConsecutiveFailures.Should().Be(1);
        broken.LastError.Should().Contain("NXDOMAIN");
    }

    [DockerFact]
    public async Task Treats_a_hostname_that_has_only_ever_failed_as_having_no_certificate()
    {
        // RecordFailureAsync writes a placeholder row -- hostname, error, failure count, no key.
        // Counting that as "held" is how a completely dark platform reported "3 missing" while
        // nine hostnames were refusing every handshake, understating the outage by a factor of
        // three in the one number an operator reads first. The row must still be reissued, and
        // exactly once: it is already due (NotAfter is -infinity), so ordering it as missing too
        // would spend two of the CA's ~50 weekly certificates on one hostname.
        var store = BuildStore();
        await store.RecordFailureAsync(
            "admin.highgeek.eu", "Vault Transit was requested but Vault is not configured.",
            TestContext.Current.CancellationToken);

        var issuer = new FakeAcme();
        var service = BuildSweep(issuer, allowed: ["admin.highgeek.eu"], backoffSeconds: 0);

        await RunOneSweepAsync(service, until: () => issuer.Ordered.Count >= 1);

        issuer.Ordered.Should().ContainSingle().Which.Should().Be("admin.highgeek.eu");

        using var scope = services.CreateScope();
        var row = await scope.ServiceProvider.GetRequiredService<EdgeDbContext>()
            .Certificates.AsNoTracking()
            .FirstAsync(c => c.Hostname == "admin.highgeek.eu", TestContext.Current.CancellationToken);
        row.EncryptedPrivateKey.Should().NotBeEmpty();
        row.LastError.Should().BeNull();
    }

    [DockerFact]
    public async Task Clears_the_backoff_on_hostnames_that_have_never_held_a_certificate()
    {
        // What makes fixing the cause enough. Six failures put a hostname ~2.7 hours out, and
        // the backoff cannot tell a CA that keeps refusing from a store of ours that was
        // broken -- so without this an operator repairs Vault, restarts, and watches a fixed
        // platform stay dark all afternoon. Bounded on purpose: only hostnames holding no
        // certificate, and only once per process start.
        var store = BuildStore();
        for (var i = 0; i < 6; i++)
        {
            await store.RecordFailureAsync(
                "admin.highgeek.eu", "Vault Transit was requested but Vault is not configured.",
                TestContext.Current.CancellationToken);
        }

        var (chainPem, keyPem) = SelfSigned("healthy.tenant.example");
        await store.SaveAsync(
            "healthy.tenant.example", chainPem, keyPem, CertificateSource.DcmsManaged,
            TestContext.Current.CancellationToken);
        await store.RecordFailureAsync(
            "healthy.tenant.example", "a real CA refusal", TestContext.Current.CancellationToken);

        var cleared = await store.ClearBackoffForNeverIssuedAsync(TestContext.Current.CancellationToken);

        cleared.Should().Be(1);

        using var scope = services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<EdgeDbContext>();

        var never = await db.Certificates.AsNoTracking()
            .FirstAsync(c => c.Hostname == "admin.highgeek.eu", TestContext.Current.CancellationToken);
        never.ConsecutiveFailures.Should().Be(0);
        never.LastError.Should().BeNull();

        // A hostname that HAS a certificate keeps its backoff: that failure was the CA's answer
        // about a renewal, and clearing it would spend the rate limit re-asking a settled
        // question.
        var healthy = await db.Certificates.AsNoTracking()
            .FirstAsync(c => c.Hostname == "healthy.tenant.example", TestContext.Current.CancellationToken);
        healthy.ConsecutiveFailures.Should().Be(1);
    }

    [DockerFact]
    public async Task Orders_nothing_at_all_when_Vault_Transit_is_unusable()
    {
        // Every private key is encrypted through Transit, so an order placed while it is broken
        // cannot be stored -- but it has still been counted against the CA. Let's Encrypt allows
        // ~50 certificates per registered domain per week and every managed subdomain shares
        // one, so a sweep that kept ordering into a broken Transit would spend the week's whole
        // allowance in an afternoon. The ten-minute Vault fix would then be followed by seven
        // days of being unable to issue anything.
        var issuer = new FakeAcme();
        var service = BuildSweep(issuer, allowed: ["admin.highgeek.eu", "shop.tenant.example"], transitBroken: true);

        await service.StartAsync(TestContext.Current.CancellationToken);
        await Task.Delay(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);
        await service.StopAsync(CancellationToken.None);

        issuer.Ordered.Should().BeEmpty();
    }

    [DockerFact]
    public async Task Does_not_back_off_when_the_failure_was_ours_rather_than_the_CAs()
    {
        // The backoff protects the CA's rate limit from a hostname whose authorization keeps
        // failing. A certificate that was ISSUED and then could not be stored is our outage, not
        // the CA's answer -- and applying the doubling backoff to it is what turns a ten-minute
        // repair into hours of the sweep skipping the hostname for a wait no CA asked for. That
        // is precisely what happened when the edge had no Vault AppRole.
        var issuer = new FakeAcme();
        var service = BuildSweep(issuer, allowed: ["admin.highgeek.eu"], storeBroken: true);

        await RunOneSweepAsync(service, until: () => issuer.Ordered.Count >= 1);

        using var scope = services.CreateScope();
        var row = await scope.ServiceProvider.GetRequiredService<EdgeDbContext>()
            .Certificates.AsNoTracking()
            .FirstOrDefaultAsync(c => c.Hostname == "admin.highgeek.eu", TestContext.Current.CancellationToken);

        // No failure row at all, so nothing to back off from: the next pass retries immediately.
        row?.ConsecutiveFailures.Should().Be(0);
    }

    /// <summary>
    /// Starts the background service, waits for the first pass to have done its work, then stops
    /// it. The sweep runs once immediately on start rather than after a full period, precisely
    /// so a deployment that has been down over a renewal window catches up now and not in an
    /// hour — which is also what makes it observable here.
    /// </summary>
    private static async Task RunOneSweepAsync(IHostedService service, Func<bool> until)
    {
        await service.StartAsync(TestContext.Current.CancellationToken);
        try
        {
            var deadline = DateTime.UtcNow.AddSeconds(30);
            while (!until() && DateTime.UtcNow < deadline)
            {
                await Task.Delay(50, TestContext.Current.CancellationToken);
            }
        }
        finally
        {
            await service.StopAsync(CancellationToken.None);
        }
    }

    private EdgeCerts.CertificateRenewalService BuildSweep(
        FakeAcme issuer,
        string[] allowed,
        bool transitBroken = false,
        bool storeBroken = false,
        int backoffSeconds = 300)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:Postgres"] = postgres.GetConnectionString(),
            })
            .Build();

        var container = transitBroken ? ServicesWith<BrokenTransit>()
            : storeBroken ? ServicesWith<ProbeOnlyTransit>()
            : services;

        return new EdgeCerts.CertificateRenewalService(
            container,
            new EdgeCerts.CertificateStore(container, new MemoryCache(new MemoryCacheOptions()),
                TimeProvider.System, NullLogger<EdgeCerts.CertificateStore>.Instance),
            new FakeAllowList(allowed),
            issuer,
            Options.Create(new EdgeCerts.CertificateOptions
            {
                TlsEnabled = true,
                FailureBackoffSeconds = backoffSeconds,
            }),
            configuration,
            new DcmsMetrics(meters),
            TimeProvider.System,
            NullLogger<EdgeCerts.CertificateRenewalService>.Instance);
    }

    /// <summary>The same container, with a different Transit implementation.</summary>
    private ServiceProvider ServicesWith<T>() where T : class, ITransitEncryptor
    {
        var collection = new ServiceCollection();
        collection.AddSingleton<ITransitEncryptor, T>();
        collection.AddDbContext<EdgeDbContext>(options =>
            options.UseNpgsql(postgres.GetConnectionString(), npgsql =>
                npgsql.MigrationsHistoryTable("__ef_migrations_history", EdgeDbContext.Schema)));
        return collection.BuildServiceProvider();
    }

    private EdgeCerts.CertificateStore BuildStore()
        => new(services, new MemoryCache(new MemoryCacheOptions()),
            TimeProvider.System, NullLogger<EdgeCerts.CertificateStore>.Instance);

    private async Task SeedAsync(string hostname)
    {
        var (chainPem, keyPem) = SelfSigned(hostname);
        await BuildStore().SaveAsync(
            hostname, chainPem, keyPem, CertificateSource.DcmsManaged, TestContext.Current.CancellationToken);
    }

    private static (string ChainPem, string KeyPem) SelfSigned(string hostname)
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest($"CN={hostname}", key, HashAlgorithmName.SHA256);
        var san = new SubjectAlternativeNameBuilder();
        san.AddDnsName(hostname);
        request.CertificateExtensions.Add(san.Build());

        using var certificate = request.CreateSelfSigned(
            DateTimeOffset.UtcNow.AddDays(-2), DateTimeOffset.UtcNow.AddDays(90));

        return (certificate.ExportCertificatePem(), key.ExportPkcs8PrivateKeyPem());
    }

    /// <summary>Stands in for site-host plus the platform's own hostnames.</summary>
    private sealed class FakeAllowList(string[] allowed) : EdgeCerts.ITlsAllowList
    {
        public Task<bool> IsAllowedAsync(string hostname, CancellationToken ct)
            => Task.FromResult(allowed.Contains(hostname, StringComparer.OrdinalIgnoreCase));

        public Task<IReadOnlyList<string>> AllowedHostnamesAsync(CancellationToken ct)
            => Task.FromResult<IReadOnlyList<string>>(allowed);
    }

    /// <summary>An ACME server that always succeeds, except for one hostname.</summary>
    private sealed class FakeAcme(string? failFor = null) : EdgeCerts.IAcmeIssuer
    {
        private readonly Lock gate = new();

        public List<string> Ordered { get; } = [];

        public Task<EdgeCerts.IssuedCertificate> IssueAsync(string hostname, CancellationToken ct)
        {
            lock (gate)
            {
                Ordered.Add(hostname);
            }

            if (hostname == failFor)
            {
                throw new InvalidOperationException("DNS problem: NXDOMAIN looking up A for " + hostname);
            }

            var (chainPem, keyPem) = SelfSigned(hostname);
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

    /// <summary>
    /// Answers the sweep's Transit probe and fails everything else — a store that is broken in a
    /// way the health check cannot see.
    /// </summary>
    private sealed class ProbeOnlyTransit : ITransitEncryptor
    {
        public Task<string> EncryptAsync(string keyName, ReadOnlyMemory<byte> plaintext, CancellationToken ct = default)
            => System.Text.Encoding.UTF8.GetString(plaintext.Span) == "edge-sweep"
                ? Task.FromResult("vault:v1:" + Convert.ToBase64String(plaintext.Span))
                : throw new InvalidOperationException("transit/encrypt failed");

        public Task<byte[]> DecryptAsync(string keyName, string ciphertext, CancellationToken ct = default)
            => Task.FromResult(Convert.FromBase64String(ciphertext["vault:v1:".Length..]));
    }

    private sealed class BrokenTransit : ITransitEncryptor
    {
        public Task<string> EncryptAsync(string keyName, ReadOnlyMemory<byte> plaintext, CancellationToken ct = default)
            => throw new InvalidOperationException("permission denied on transit/encrypt/" + keyName);

        public Task<byte[]> DecryptAsync(string keyName, string ciphertext, CancellationToken ct = default)
            => throw new InvalidOperationException("permission denied on transit/decrypt/" + keyName);
    }

    private sealed class TestMeterFactory : IMeterFactory
    {
        private readonly List<Meter> created = [];

        public Meter Create(MeterOptions options)
        {
            var meter = new Meter(options.Name, options.Version, options.Tags, scope: this);
            created.Add(meter);
            return meter;
        }

        public void Dispose()
        {
            foreach (var meter in created)
            {
                meter.Dispose();
            }
            created.Clear();
        }
    }
}
