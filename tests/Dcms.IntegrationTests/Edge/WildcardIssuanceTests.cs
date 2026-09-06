extern alias EdgeApp;

using System.Net.Http.Json;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Dcms.Shared.Caching;
using Dcms.Shared.Data.Edge;
using Dcms.Shared.Vault;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Configurations;
using DotNet.Testcontainers.Containers;
using DotNet.Testcontainers.Networks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Testcontainers.PostgreSql;
using EdgeCerts = EdgeApp::Dcms.Edge.Certificates;
using EdgeDns = EdgeApp::Dcms.Edge.Certificates.Dns;

namespace Dcms.IntegrationTests.Edge;

/// <summary>
/// Wildcard issuance driven end to end against Pebble, the ACME test CA the Let's Encrypt team
/// publishes, with <c>pebble-challtestsrv</c> as the DNS server it validates against.
///
/// <para><b>Why a real ACME server and not a mock.</b> The mistake this path is most likely to
/// contain cannot be reproduced without one. An order for <c>example.test</c> and
/// <c>*.example.test</c> produces two authorizations, and ACME strips the wildcard label, so both
/// publish at <c>_acme-challenge.example.test</c> — two different values, at one name, that must
/// be resolvable at the same time. An implementation that <i>replaces</i> the record satisfies
/// the second authorization and fails the first. Nothing about our own code looks wrong; only a
/// CA actually validating both says so.</para>
///
/// <para>The second test here deliberately breaks it in exactly that way and asserts the order
/// fails, so this file cannot quietly stop testing anything.</para>
///
/// <para>Pebble does not enforce rate limits and validates from inside a container network, so
/// this proves our half of the conversation and nothing about Let's Encrypt's. See
/// <c>infra/acme-test/README.md</c> and <c>docs/adr/0011-wildcard-tls-dns01.md</c>.</para>
/// </summary>
public sealed class WildcardIssuanceTests : IAsyncLifetime
{
    private const string PebbleImage = "ghcr.io/letsencrypt/pebble:2.6.0";
    private const string ChallTestSrvImage = "ghcr.io/letsencrypt/pebble-challtestsrv:2.6.0";

    private readonly PostgreSqlContainer postgres = TestPostgres.Build();
    private INetwork network = null!;
    private IContainer challtestsrv = null!;
    private IContainer pebble = null!;
    private ServiceProvider services = null!;
    private HttpClient management = null!;

    public async ValueTask InitializeAsync()
    {
        await postgres.StartAsync();

        network = new NetworkBuilder().Build();
        await network.CreateAsync();

        // Every challenge responder off: this stack only ever asks it to hold TXT records, and
        // -defaultIPv4 empty so it answers only what is explicitly added rather than pointing
        // every A query at one address.
        challtestsrv = new ContainerBuilder(ChallTestSrvImage)
            .WithNetwork(network)
            .WithNetworkAliases("challtestsrv")
            .WithCommand(
                "-dns01", ":8053",
                "-management", ":8055",
                "-defaultIPv4", "",
                "-http01", "",
                "-https01", "",
                "-tlsalpn01", "")
            .WithPortBinding(8055, true)
            .Build();
        await challtestsrv.StartAsync();

        // -dnsserver points at challtestsrv, which is the whole point: Docker's own resolver
        // knows container names and can never answer a TXT query, and for DNS-01 the TXT record
        // IS the proof.
        pebble = new ContainerBuilder(PebbleImage)
            .WithNetwork(network)
            .WithNetworkAliases("pebble")
            // Pebble sleeps a random 0-15s before validating, to shake out clients that assume
            // it is instant. Ours polls, so the delay only makes the test slower.
            .WithEnvironment("PEBBLE_VA_NOSLEEP", "1")
            .WithResourceMapping(
                Encoding.UTF8.GetBytes(PebbleConfig), "/test/config/pebble-config.json")
            .WithCommand(
                "-config", "/test/config/pebble-config.json",
                "-dnsserver", "challtestsrv:8053")
            .WithPortBinding(14000, true)
            .Build();
        await pebble.StartAsync();

        // Readiness is polled from HERE rather than declared as a Testcontainers wait strategy.
        // Both images are built FROM scratch and carry no shell, so UntilInternalTcpPortIsAvailable
        // -- which runs a command inside the container -- can never succeed and simply hangs.
        await WaitForAsync(
            $"http://{challtestsrv.Hostname}:{challtestsrv.GetMappedPublicPort(8055)}/print-request-history");
        await WaitForAsync(
            $"https://{pebble.Hostname}:{pebble.GetMappedPublicPort(14000)}/dir");

        management = new HttpClient
        {
            BaseAddress = new Uri(
                $"http://{challtestsrv.Hostname}:{challtestsrv.GetMappedPublicPort(8055)}"),
        };

        var collection = new ServiceCollection();
        collection.AddSingleton<ITransitEncryptor, PassthroughTransit>();
        collection.AddDbContext<EdgeDbContext>(options =>
            options.UseNpgsql(postgres.GetConnectionString(), npgsql =>
                npgsql.MigrationsHistoryTable("__ef_migrations_history", EdgeDbContext.Schema)));
        services = collection.BuildServiceProvider();

        using var scope = services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<EdgeDbContext>().Database.MigrateAsync();
    }

    /// <summary>
    /// Polls until the address answers at all. Any HTTP response counts: the question is whether
    /// the listener is up, and Pebble's self-signed certificate means the handler has to be
    /// permissive anyway.
    /// </summary>
    private static async Task WaitForAsync(string url)
    {
        using var handler = new HttpClientHandler
        {
            ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator,
        };
        using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(2) };

        var deadline = DateTimeOffset.UtcNow.AddSeconds(60);
        while (true)
        {
            try
            {
                using var response = await client.GetAsync(url);
                return;
            }
            catch when (DateTimeOffset.UtcNow < deadline)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(250));
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        management?.Dispose();
        if (services is not null)
        {
            await services.DisposeAsync();
        }

        if (pebble is not null)
        {
            await pebble.DisposeAsync();
        }

        if (challtestsrv is not null)
        {
            await challtestsrv.DisposeAsync();
        }

        if (network is not null)
        {
            await network.DisposeAsync();
        }

        await postgres.DisposeAsync();
    }

    /// <summary>
    /// The case the whole design turns on: an apex and its wildcard, ordered together, validated
    /// by a real CA. Both authorizations resolve against one record name carrying two values.
    /// </summary>
    [DockerFact]
    public async Task Issues_one_certificate_covering_an_apex_and_its_wildcard()
    {
        var issuer = BuildIssuer(new ManagementDnsWriter(management));

        var issued = await issuer.IssueAsync(
            ["example.test", "*.example.test"], TestContext.Current.CancellationToken);

        var leaf = X509Certificate2.CreateFromPem(issued.PemChain);
        EdgeCerts.CertificateStore.ReadSubjectAlternativeNames(leaf)
            .Should().BeEquivalentTo(["example.test", "*.example.test"]);
    }

    /// <summary>
    /// The same order, with a writer that <b>replaces</b> the record instead of adding to it —
    /// the single most common way a DNS-01 implementation is wrong.
    ///
    /// <para>This exists so the test above cannot pass for the wrong reason. If both tests were
    /// green with a replacing writer, neither would be proving anything: it would mean Pebble
    /// was validating one authorization against a value published for the other, or not
    /// validating at all.</para>
    /// </summary>
    [DockerFact]
    public async Task Fails_when_the_second_challenge_record_replaces_the_first()
    {
        var issuer = BuildIssuer(new ManagementDnsWriter(management, replaceInsteadOfAdd: true));

        var act = async () => await issuer.IssueAsync(
            ["example.test", "*.example.test"], TestContext.Current.CancellationToken);

        // AcmeIssuanceException specifically, and not merely "something threw". A broken test
        // harness -- a wrong management endpoint, an unreachable container -- also throws, and
        // this assertion is worthless if it accepts that. This is the CA refusing an
        // authorization, which is the only failure that proves the point.
        (await act.Should().ThrowAsync<EdgeCerts.AcmeIssuanceException>(
                "the authorization whose value was overwritten cannot be validated"))
            .Which.Message.Should().Contain("could not validate");
    }

    /// <summary>A single wildcard, which needs DNS-01 but not the two-values-at-one-name case.</summary>
    [DockerFact]
    public async Task Issues_a_wildcard_on_its_own()
    {
        var issuer = BuildIssuer(new ManagementDnsWriter(management));

        var issued = await issuer.IssueAsync(
            ["*.solo.test"], TestContext.Current.CancellationToken);

        var leaf = X509Certificate2.CreateFromPem(issued.PemChain);
        EdgeCerts.CertificateStore.ReadSubjectAlternativeNames(leaf)
            .Should().BeEquivalentTo(["*.solo.test"]);
    }

    private EdgeCerts.CertesAcmeIssuer BuildIssuer(EdgeDns.IDnsChallengeWriter writer)
    {
        var certificates = Options.Create(new EdgeCerts.CertificateOptions
        {
            AcmeDirectory = $"https://{pebble.Hostname}:{pebble.GetMappedPublicPort(14000)}/dir",
            ContactEmail = "acme-test@example.invalid",
            // Pebble serves its API over a self-signed certificate by design. The switch is
            // refused outright for a Let's Encrypt directory, which is what keeps it honest.
            AcceptInsecureAcmeDirectory = true,
        });

        var dns = Options.Create(new EdgeDns.DnsOptions
        {
            // Short: nothing here is authoritative to the system resolver, so the waiter cannot
            // confirm propagation and falls through by design. challtestsrv is updated
            // synchronously before Validate is called, so there is nothing to wait for.
            PropagationTimeoutSeconds = 1,
            PropagationPollSeconds = 1,
        });

        return new EdgeCerts.CertesAcmeIssuer(
            services,
            new EdgeCerts.AcmeChallengeStore(new UnusedCache()),
            writer,
            new EdgeDns.DnsPropagationWaiter(
                dns, TimeProvider.System, NullLogger<EdgeDns.DnsPropagationWaiter>.Instance),
            certificates,
            TimeProvider.System,
            NullLogger<EdgeCerts.CertesAcmeIssuer>.Instance);
    }

    /// <summary>
    /// Publishes into challtestsrv's management API — <c>/set-txt</c> adds one value at a name
    /// (it appends, despite the name), <c>/clear-txt</c> removes every value at it.
    /// </summary>
    /// <param name="replaceInsteadOfAdd">
    /// Clears the name before adding, reproducing a writer that uses PUT semantics. Only the
    /// negative test sets this.
    /// </param>
    private sealed class ManagementDnsWriter(HttpClient http, bool replaceInsteadOfAdd = false)
        : EdgeDns.IDnsChallengeWriter
    {
        public async Task<EdgeDns.DnsChallengeRecord> AddTxtAsync(
            string name, string value, CancellationToken ct)
        {
            var fqdn = name.EndsWith('.') ? name : name + ".";

            if (replaceInsteadOfAdd)
            {
                using var clear = await http.PostAsJsonAsync("/clear-txt", new { host = fqdn }, ct);
                clear.EnsureSuccessStatusCode();
            }

            // "/set-txt" APPENDS, despite the name -- it pushes onto the list of values held at
            // that host. That is the whole reason this server can reproduce the apex+wildcard
            // case, and it is verified rather than assumed: two calls answer with two records.
            using var response = await http.PostAsJsonAsync("/set-txt", new { host = fqdn, value }, ct);
            response.EnsureSuccessStatusCode();
            return new EdgeDns.DnsChallengeRecord("challtestsrv", fqdn, name);
        }

        public async Task RemoveAsync(EdgeDns.DnsChallengeRecord record, CancellationToken ct)
        {
            using var response = await http.PostAsJsonAsync(
                "/clear-txt", new { host = record.RecordId }, ct);
            _ = response.IsSuccessStatusCode;
        }

        public Task<bool> CanPublishForAsync(string identifier, CancellationToken ct)
            => Task.FromResult(true);
    }

    /// <summary>The HTTP-01 challenge store, which a DNS-01 order never touches.</summary>
    private sealed class UnusedCache : ICacheService
    {
        public Task<T?> GetAsync<T>(string key, CancellationToken ct = default)
            => throw new NotSupportedException("A DNS-01 order must not touch the HTTP-01 store.");

        public Task SetAsync<T>(string key, T value, TimeSpan? ttl = null, CancellationToken ct = default)
            => throw new NotSupportedException("A DNS-01 order must not touch the HTTP-01 store.");

        public Task RemoveAsync(string key, CancellationToken ct = default)
            => throw new NotSupportedException("A DNS-01 order must not touch the HTTP-01 store.");

        public Task<long> IncrementAsync(string key, CancellationToken ct = default)
            => throw new NotSupportedException("A DNS-01 order must not touch the HTTP-01 store.");
    }

    private sealed class PassthroughTransit : ITransitEncryptor
    {
        public Task<string> EncryptAsync(string keyName, ReadOnlyMemory<byte> plaintext, CancellationToken ct = default)
            => Task.FromResult("vault:v1:" + Convert.ToBase64String(plaintext.Span));

        public Task<byte[]> DecryptAsync(string keyName, string ciphertext, CancellationToken ct = default)
            => Task.FromResult(Convert.FromBase64String(ciphertext["vault:v1:".Length..]));
    }

    /// <summary>
    /// Mirrors <c>infra/acme-test/pebble-config.json</c>, inlined so the test does not depend on
    /// a path relative to the repository root.
    /// </summary>
    private const string PebbleConfig = """
        {
          "pebble": {
            "listenAddress": "0.0.0.0:14000",
            "managementListenAddress": "0.0.0.0:15000",
            "certificate": "test/certs/localhost/cert.pem",
            "privateKey": "test/certs/localhost/key.pem",
            "httpPort": 8080,
            "tlsPort": 5001,
            "ocspResponderURL": "",
            "externalAccountBindingRequired": false
          }
        }
        """;
}
