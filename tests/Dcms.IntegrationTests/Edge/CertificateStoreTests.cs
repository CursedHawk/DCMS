extern alias EdgeApp;

using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Dcms.Shared.Data.Edge;
using Dcms.Shared.Vault;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Testcontainers.PostgreSql;
using EdgeCerts = EdgeApp::Dcms.Edge.Certificates;

namespace Dcms.IntegrationTests.Edge;

/// <summary>
/// The certificate store against a real database.
///
/// <para>Worth an integration test rather than a mock because the two things most likely to be
/// wrong are both outside our code: whether a key that has been through Vault-Transit
/// encryption, a Postgres round trip and a PEM/PKCS#12 conversion is still usable as a TLS
/// server credential, and whether the issuing chain survives with it. Both fail at handshake
/// time on a tenant's live domain, which is the worst place to discover either.</para>
/// </summary>
public sealed class CertificateStoreTests : IAsyncLifetime
{
    private readonly PostgreSqlContainer postgres = TestPostgres.Build();
    private ServiceProvider services = null!;

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
        await services.DisposeAsync();
        await postgres.DisposeAsync();
    }

    [DockerFact]
    public async Task Round_trips_a_certificate_through_encryption_and_serves_it_as_a_credential()
    {
        var store = BuildStore();
        var (chainPem, keyPem) = SelfSigned("shop.tenant.example");

        await store.SaveAsync("shop.tenant.example", chainPem, keyPem, CertificateSource.DcmsManaged, TestContext.Current.CancellationToken);
        var credential = await store.GetAsync("shop.tenant.example", TestContext.Current.CancellationToken);

        credential.Should().NotBeNull();
        // The property that matters. CreateFromPem hands back an ephemeral key that SslStream
        // cannot always use as a server credential; the store round-trips it through PKCS#12 to
        // get one it can. If that ever regresses, handshakes fail and nothing else does.
        credential!.TargetCertificate.HasPrivateKey.Should().BeTrue();
        credential.TargetCertificate.GetNameInfo(X509NameType.SimpleName, forIssuer: false)
            .Should().Be("shop.tenant.example");
    }

    [DockerFact]
    public async Task Never_writes_the_private_key_in_plaintext()
    {
        var store = BuildStore();
        var (chainPem, keyPem) = SelfSigned("secret.tenant.example");

        await store.SaveAsync("secret.tenant.example", chainPem, keyPem, CertificateSource.DcmsManaged, TestContext.Current.CancellationToken);

        using var scope = services.CreateScope();
        var row = await scope.ServiceProvider.GetRequiredService<EdgeDbContext>()
            .Certificates.AsNoTracking()
            .FirstAsync(c => c.Hostname == "secret.tenant.example", TestContext.Current.CancellationToken);

        // This is the one secret on the platform whose disclosure lets someone impersonate a
        // tenant's site outright, so "it goes through the encryptor" is asserted against the
        // stored bytes rather than against the code path.
        row.EncryptedPrivateKey.Should().NotContain("PRIVATE KEY");
        row.EncryptedPrivateKey.Should().NotBe(keyPem);
        row.PemChain.Should().Contain("BEGIN CERTIFICATE");
    }

    [DockerFact]
    public async Task Refuses_to_serve_an_expired_certificate()
    {
        var store = BuildStore();
        var (chainPem, keyPem) = SelfSigned("expired.tenant.example", notAfter: DateTimeOffset.UtcNow.AddDays(-1));

        await store.SaveAsync("expired.tenant.example", chainPem, keyPem, CertificateSource.DcmsManaged, TestContext.Current.CancellationToken);
        var credential = await store.GetAsync("expired.tenant.example", TestContext.Current.CancellationToken);

        // Serving it would put a browser warning on the tenant's own domain. Returning nothing
        // lets the provisioner replace it instead.
        credential.Should().BeNull();
    }

    [DockerFact]
    public async Task Backs_off_further_after_each_consecutive_failure()
    {
        var store = BuildStore();
        var options = new EdgeCerts.CertificateOptions { FailureBackoffSeconds = 60 };

        await store.RecordFailureAsync("broken.tenant.example", "DNS problem: NXDOMAIN", TestContext.Current.CancellationToken);
        var afterOne = await store.RetryNotBeforeAsync("broken.tenant.example", options, TestContext.Current.CancellationToken);

        await store.RecordFailureAsync("broken.tenant.example", "DNS problem: NXDOMAIN", TestContext.Current.CancellationToken);
        var afterTwo = await store.RetryNotBeforeAsync("broken.tenant.example", options, TestContext.Current.CancellationToken);

        afterOne.Should().NotBeNull();
        afterTwo.Should().NotBeNull();
        // Doubling, so a domain that has been broken for hours stops costing the CA anything.
        (afterTwo!.Value - afterOne!.Value).Should().BeGreaterThan(TimeSpan.FromSeconds(30));
    }

    [DockerFact]
    public async Task A_successful_issue_clears_the_recorded_failure()
    {
        var store = BuildStore();
        var (chainPem, keyPem) = SelfSigned("recovered.tenant.example");

        await store.RecordFailureAsync("recovered.tenant.example", "DNS problem: NXDOMAIN", TestContext.Current.CancellationToken);
        await store.SaveAsync("recovered.tenant.example", chainPem, keyPem, CertificateSource.DcmsManaged, TestContext.Current.CancellationToken);

        using var scope = services.CreateScope();
        var row = await scope.ServiceProvider.GetRequiredService<EdgeDbContext>()
            .Certificates.AsNoTracking()
            .FirstAsync(c => c.Hostname == "recovered.tenant.example", TestContext.Current.CancellationToken);

        // Otherwise the admin UI keeps showing an error next to a domain that is working, and
        // the backoff keeps throttling a domain that no longer needs it.
        row.LastError.Should().BeNull();
        row.ConsecutiveFailures.Should().Be(0);
    }

    private EdgeCerts.CertificateStore BuildStore()
        => new(services, new MemoryCache(new MemoryCacheOptions()),
            TimeProvider.System, NullLogger<EdgeCerts.CertificateStore>.Instance);

    private static (string ChainPem, string KeyPem) SelfSigned(string hostname, DateTimeOffset? notAfter = null)
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest($"CN={hostname}", key, HashAlgorithmName.SHA256);
        var san = new SubjectAlternativeNameBuilder();
        san.AddDnsName(hostname);
        request.CertificateExtensions.Add(san.Build());

        using var certificate = request.CreateSelfSigned(
            DateTimeOffset.UtcNow.AddDays(-2), notAfter ?? DateTimeOffset.UtcNow.AddDays(90));

        return (certificate.ExportCertificatePem(), key.ExportPkcs8PrivateKeyPem());
    }

    /// <summary>
    /// Stands in for Vault Transit. Base64 rather than a no-op so the "never plaintext" assertion
    /// is testing that the store actually routes the key through the encryptor, rather than
    /// passing because the fake happened to return its input.
    /// </summary>
    private sealed class PassthroughTransit : ITransitEncryptor
    {
        public Task<string> EncryptAsync(string keyName, ReadOnlyMemory<byte> plaintext, CancellationToken ct = default)
            => Task.FromResult("vault:v1:" + Convert.ToBase64String(plaintext.Span));

        public Task<byte[]> DecryptAsync(string keyName, string ciphertext, CancellationToken ct = default)
            => Task.FromResult(Convert.FromBase64String(ciphertext["vault:v1:".Length..]));
    }
}
