extern alias AdminApiApp;
using Dcms.IntegrationTests.Social;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Minio;
using Minio.DataModel.Args;
using NATS.Client.JetStream;
using NATS.Client.JetStream.Models;
using NATS.Net;
using Testcontainers.PostgreSql;
using Testcontainers.Minio;
using Testcontainers.Redis;
using Testcontainers.Nats;

namespace Dcms.IntegrationTests.Tenancy;

/// <summary>
/// Boots admin-api against throwaway Postgres + Redis + NATS containers, with
/// authentication replaced by a header-driven test scheme. Domain verification
/// auto-passes (Domains:AutoVerify). The TENANCY JetStream stream is created so
/// event publishing succeeds.
/// </summary>
public sealed class AdminApiFixture : IAsyncLifetime
{
    private readonly PostgreSqlContainer _postgres = TestPostgres.Build();

    private readonly RedisContainer _redis = new RedisBuilder("redis:7").Build();

    private readonly NatsContainer _nats = new NatsBuilder("nats:2.11").WithCommand("--jetstream").Build();

    /// <summary>
    /// Object storage, needed by anything that ingests media — including the Meta feed sync,
    /// which mirrors CDN images through the very same pipeline as an admin upload.
    /// </summary>
    private readonly MinioContainer _minio = TestMinio.Build();

    private const string MediaBucket = "dcms-media-test";

    public WebApplicationFactory<AdminApiApp::Program> Factory { get; private set; } = null!;

    /// <summary>
    /// In-process stub of the Meta Graph API. Started before admin-api so its dynamic URL can
    /// be handed to the app as Social:OverrideBaseUrl — the app has to be told where Meta is at
    /// construction time, which is why this lives on the fixture rather than in a test.
    /// Inert unless a test drives it.
    /// </summary>
    public MetaStubServer MetaStub { get; private set; } = null!;

    /// <summary>Raw Postgres connection string (as the owner) — used by the RLS isolation test.</summary>
    public string PostgresConnectionString => _postgres.GetConnectionString();

    public async ValueTask InitializeAsync()
    {
        await Task.WhenAll(_postgres.StartAsync(), _redis.StartAsync(), _nats.StartAsync(), _minio.StartAsync());

        var minioEndpoint = $"{_minio.Hostname}:{_minio.GetMappedPublicPort(9000)}";
        await new MinioClient()
            .WithEndpoint(minioEndpoint)
            .WithCredentials(_minio.GetAccessKey(), _minio.GetSecretKey())
            .Build()
            .MakeBucketAsync(new MakeBucketArgs().WithBucket(MediaBucket));

        await using (var nats = new NatsClient(_nats.GetConnectionString()))
        {
            var js = nats.CreateJetStreamContext();
            await js.CreateStreamAsync(new StreamConfig("TENANCY", ["tenant.>", "plugin.instance.>", "membership.>"]));
            await js.CreateStreamAsync(new StreamConfig("CMS", ["content.>"]));
            // The chain writer fans every appended record out to this after it commits. Not
            // needed for the record itself — that path is a row in the same transaction — but
            // without the stream the fan-out logs an error on every test that changes anything.
            await js.CreateStreamAsync(new StreamConfig("AUDIT", ["audit.>"]));
            // Media ingest publishes a processing request; without the stream every mirrored
            // image logs a publish failure.
            await js.CreateStreamAsync(new StreamConfig("MEDIA", ["media.process.>"]));
        }

        MetaStub = await MetaStubServer.StartAsync();

        if (RlsEnforced)
        {
            // Migrations and RlsConfigurator are DDL, which the enforcing role cannot run. So
            // boot once as the owner to build the schema -- exactly what the migrate job does
            // in a deploy -- then grant dcms_app and boot the app under test as that role.
            await using (var migrate = Create(_postgres.GetConnectionString(), migrate: true, enforce: false, minioEndpoint))
            {
                using var _ = migrate.CreateClient();
            }
            await AppRole.GrantAsync(_postgres.GetConnectionString());
            Factory = Create(AppRole.ConnectionString(_postgres.GetConnectionString()), migrate: false, enforce: true, minioEndpoint);
        }
        else
        {
            Factory = Create(_postgres.GetConnectionString(), migrate: true, enforce: false, minioEndpoint);
        }

        using var client = Factory.CreateClient();
    }

    /// <summary>
    /// ADR 0015. Set <c>DCMS_TEST_RLS_ENFORCE=1</c> to run this collection the way a service runs
    /// after phase 4: connected as <c>dcms_app</c>, <c>NOBYPASSRLS</c>, with the tenant GUC
    /// interceptor on. Every test then proves its path works under the database's isolation, not
    /// only EF's -- which is the evidence the IgnoreQueryFilters() sweep is judged by.
    /// </summary>
    public static bool RlsEnforced => Environment.GetEnvironmentVariable("DCMS_TEST_RLS_ENFORCE") == "1";

    private WebApplicationFactory<AdminApiApp::Program> Create(string postgres, bool migrate, bool enforce, string minioEndpoint)
        => new WebApplicationFactory<AdminApiApp::Program>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("Rls:Enforce", enforce ? "true" : "false");
            if (FileErrorSink.PathFromEnvironment is { } logPath)
            {
                builder.ConfigureTestServices(services =>
                    services.AddSingleton<Serilog.Core.ILogEventSink>(new FileErrorSink(logPath)));
            }
            // A Meta app has to look configured or the connect endpoint returns 501. The
            // secrets are nonsense on purpose: every call goes to the stub.
            builder.UseSetting("Social:Meta:AppId", "test-fb-app");
            builder.UseSetting("Social:Meta:AppSecret", "test-fb-secret");
            builder.UseSetting("Social:Instagram:AppId", "test-ig-app");
            builder.UseSetting("Social:Instagram:AppSecret", "test-ig-secret");
            builder.UseSetting("Social:RedirectUri", "https://admin.test/api/admin/social/callback");
            builder.UseSetting("Social:OverrideBaseUrl", MetaStub.BaseUrl);
            // The background sync is driven explicitly by tests through the sync-now endpoint,
            // so a timer firing underneath them would only add nondeterminism.
            builder.UseSetting("Social:SyncEnabled", "false");

            builder.UseSetting("Storage:Endpoint", minioEndpoint);
            builder.UseSetting("Storage:AccessKey", _minio.GetAccessKey());
            builder.UseSetting("Storage:SecretKey", _minio.GetSecretKey());
            builder.UseSetting("Storage:UseSsl", "false");
            builder.UseSetting("Storage:MediaBucket", MediaBucket);
            builder.UseSetting("ConnectionStrings:Postgres", postgres);
            builder.UseSetting("ConnectionStrings:Redis", _redis.GetConnectionString());
            builder.UseSetting("Nats:Url", _nats.GetConnectionString());
            builder.UseSetting("Tenancy:Migrate", migrate ? "true" : "false");
            builder.UseSetting("Domains:AutoVerify", "true");
            builder.UseSetting("Scheduler:PollSeconds", "1"); // fast scheduler for tests

            builder.ConfigureTestServices(services =>
            {
                services.AddAuthentication(TestAuthHandler.SchemeName)
                    .AddScheme<AuthenticationSchemeOptions, TestAuthHandler>(TestAuthHandler.SchemeName, _ => { });

                // No Vault in tests. Reversible and clearly-marked, so an assertion that a
                // token never reaches an API response still means something.
                services.AddSingleton<Dcms.Shared.Vault.ITransitEncryptor, FakeTransitEncryptor>();
            });
        });

    public async ValueTask DisposeAsync()
    {
        if (Factory is not null)
        {
            await Factory.DisposeAsync();
        }
        if (MetaStub is not null)
        {
            await MetaStub.DisposeAsync();
        }
        await Task.WhenAll(
            _postgres.DisposeAsync().AsTask(),
            _redis.DisposeAsync().AsTask(),
            _nats.DisposeAsync().AsTask(),
            _minio.DisposeAsync().AsTask());
    }
}

[CollectionDefinition(Name)]
public sealed class AdminApiCollection : ICollectionFixture<AdminApiFixture>
{
    public const string Name = "admin-api";
}
