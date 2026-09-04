extern alias EdgeApp;

using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
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
/// The one-shot import that turns the Caddy cutover into a port swap.
///
/// <para>It runs exactly once, on the deploy that moves :443 from Caddy to the edge, and there
/// is no second chance to get it right: whatever it fails to import has to be reissued inside a
/// visitor's handshake, and every live tenant domain arriving at Let's Encrypt in the same
/// minute is how a cutover becomes a week-long rate-limit block. So the file layout, the
/// hostname it derives, and each thing it declines to import are asserted here against real
/// certificates on a real disk rather than reasoned about.</para>
/// </summary>
public sealed class CaddyCertificateImporterTests : IAsyncLifetime
{
    private readonly PostgreSqlContainer postgres = TestPostgres.Build();
    private ServiceProvider services = null!;
    private string caddyData = null!;

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

        caddyData = Directory.CreateTempSubdirectory("caddy-import-test").FullName;
    }

    public async ValueTask DisposeAsync()
    {
        await services.DisposeAsync();
        await postgres.DisposeAsync();
        try
        {
            Directory.Delete(caddyData, recursive: true);
        }
        catch (DirectoryNotFoundException)
        {
        }
    }

    [DockerFact]
    public async Task Takes_the_hostname_from_the_certificate_not_the_directory_it_sits_in()
    {
        // Caddy's directory name is a filename-safe encoding of the subject, not the subject:
        // a wildcard is stored as `wildcard_.example.com`. Trusting it would key the store on a
        // hostname no client ever sends, and the certificate would never be found again.
        WriteCaddyCertificate("wildcard_.tenant.example", SelfSigned("shop.tenant.example"));

        await ImportAsync();

        var rows = await LoadRowsAsync();
        rows.Should().ContainSingle();
        rows[0].Hostname.Should().Be("shop.tenant.example");
        rows[0].PemChain.Should().Contain("BEGIN CERTIFICATE");
        // Through the encryptor, exactly as an issued certificate would be. The import is not a
        // side door into the store.
        rows[0].EncryptedPrivateKey.Should().NotContain("PRIVATE KEY");
    }

    [DockerFact]
    public async Task Never_replaces_a_certificate_the_platform_already_holds()
    {
        var store = BuildStore();
        var (ourChain, ourKey) = SelfSigned("shop.tenant.example");
        await store.SaveAsync("shop.tenant.example", ourChain, ourKey, CertificateSource.DcmsManaged, TestContext.Current.CancellationToken);

        WriteCaddyCertificate("shop.tenant.example", SelfSigned("shop.tenant.example"));

        await ImportAsync(store);

        var rows = await LoadRowsAsync();
        rows.Should().ContainSingle();
        // The import is idempotent because it runs on a deploy, and a deploy can be repeated.
        // A second pass overwriting a fresher certificate with a stale one off disk would be a
        // silent downgrade nobody would notice until it expired.
        rows[0].PemChain.Should().Be(ourChain);
    }

    [DockerFact]
    public async Task Declines_a_certificate_covering_more_than_one_name()
    {
        WriteCaddyCertificate("multi", SelfSigned("shop.tenant.example", "www.tenant.example"));

        await ImportAsync();

        // The store is keyed by a single hostname, so there is no honest row for this to become.
        // Importing it under one of its names would leave the other looking uncovered while the
        // renewal sweep quietly reissued a narrower certificate over it.
        (await LoadRowsAsync()).Should().BeEmpty();
    }

    [DockerFact]
    public async Task Declines_an_expired_certificate()
    {
        WriteCaddyCertificate(
            "stale.tenant.example",
            SelfSigned("stale.tenant.example", notAfter: DateTimeOffset.UtcNow.AddDays(-1)));

        await ImportAsync();

        // Importing it would satisfy the store's "we have one" check with something no browser
        // accepts, and the domain would serve warnings until the renewal sweep came round.
        (await LoadRowsAsync()).Should().BeEmpty();
    }

    [DockerFact]
    public async Task One_unusable_file_does_not_cost_the_others()
    {
        var directory = Path.Combine(caddyData, "caddy", "certificates", "acme-v02", "orphan.tenant.example");
        Directory.CreateDirectory(directory);
        // A .crt with no sibling .key: Caddy writes both, so this is a half-written or
        // hand-edited store rather than a normal one.
        await File.WriteAllTextAsync(
            Path.Combine(directory, "orphan.tenant.example.crt"),
            SelfSigned("orphan.tenant.example").ChainPem,
            TestContext.Current.CancellationToken);

        var garbage = Path.Combine(caddyData, "caddy", "certificates", "acme-v02", "junk.tenant.example");
        Directory.CreateDirectory(garbage);
        await File.WriteAllTextAsync(Path.Combine(garbage, "junk.tenant.example.crt"), "not a certificate", TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(Path.Combine(garbage, "junk.tenant.example.key"), "not a key", TestContext.Current.CancellationToken);

        WriteCaddyCertificate("good.tenant.example", SelfSigned("good.tenant.example"));

        await ImportAsync();

        // The failure mode this guards is the expensive one: the import throwing partway leaves
        // the certificates it had not reached yet to be issued on demand at cutover, which is
        // the burst the whole import exists to avoid.
        var rows = await LoadRowsAsync();
        rows.Should().ContainSingle();
        rows[0].Hostname.Should().Be("good.tenant.example");
    }

    [DockerFact]
    public async Task Does_nothing_when_there_is_no_Caddy_store_to_read()
    {
        // Every deploy after the cutover, and every dev machine. The path is mounted for one
        // deploy and cleared afterwards, so "absent" is the normal case and must not be an error.
        await ImportAsync();

        (await LoadRowsAsync()).Should().BeEmpty();
    }

    private async Task ImportAsync(EdgeCerts.CertificateStore? store = null)
    {
        var importer = new EdgeCerts.CaddyCertificateImporter(
            services, store ?? BuildStore(), NullLogger<EdgeCerts.CaddyCertificateImporter>.Instance);
        await importer.ImportAsync(caddyData, TestContext.Current.CancellationToken);
    }

    private async Task<List<EdgeCertificate>> LoadRowsAsync()
    {
        using var scope = services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<EdgeDbContext>()
            .Certificates.AsNoTracking()
            .OrderBy(c => c.Hostname)
            .ToListAsync(TestContext.Current.CancellationToken);
    }

    private EdgeCerts.CertificateStore BuildStore()
        => new(services, new MemoryCache(new MemoryCacheOptions()),
            TimeProvider.System, NullLogger<EdgeCerts.CertificateStore>.Instance);

    /// <summary>
    /// Caddy's on-disk layout:
    /// <c>&lt;data&gt;/caddy/certificates/&lt;ca-directory&gt;/&lt;name&gt;/&lt;name&gt;.crt</c>
    /// with a sibling <c>.key</c>.
    /// </summary>
    private void WriteCaddyCertificate(string directoryName, (string ChainPem, string KeyPem) certificate)
    {
        var directory = Path.Combine(
            caddyData, "caddy", "certificates", "acme-v02.api.letsencrypt.org-directory", directoryName);
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, $"{directoryName}.crt"), certificate.ChainPem);
        File.WriteAllText(Path.Combine(directory, $"{directoryName}.key"), certificate.KeyPem);
    }

    private static (string ChainPem, string KeyPem) SelfSigned(
        string hostname, string? alsoCovering = null, DateTimeOffset? notAfter = null)
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest($"CN={hostname}", key, HashAlgorithmName.SHA256);
        var san = new SubjectAlternativeNameBuilder();
        san.AddDnsName(hostname);
        if (alsoCovering is not null)
        {
            san.AddDnsName(alsoCovering);
        }
        request.CertificateExtensions.Add(san.Build());

        using var certificate = request.CreateSelfSigned(
            DateTimeOffset.UtcNow.AddDays(-2), notAfter ?? DateTimeOffset.UtcNow.AddDays(90));

        return (certificate.ExportCertificatePem(), key.ExportPkcs8PrivateKeyPem());
    }

    /// <summary>Stands in for Vault Transit; see <see cref="CertificateStoreTests"/>.</summary>
    private sealed class PassthroughTransit : ITransitEncryptor
    {
        public Task<string> EncryptAsync(string keyName, ReadOnlyMemory<byte> plaintext, CancellationToken ct = default)
            => Task.FromResult("vault:v1:" + Convert.ToBase64String(plaintext.Span));

        public Task<byte[]> DecryptAsync(string keyName, string ciphertext, CancellationToken ct = default)
            => Task.FromResult(Convert.FromBase64String(ciphertext["vault:v1:".Length..]));
    }
}
