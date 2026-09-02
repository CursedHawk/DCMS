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
    private readonly MinioContainer _minio = new MinioBuilder("minio/minio:latest").Build();

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

        Factory = new WebApplicationFactory<AdminApiApp::Program>().WithWebHostBuilder(builder =>
        {
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
            builder.UseSetting("ConnectionStrings:Postgres", _postgres.GetConnectionString());
            builder.UseSetting("ConnectionStrings:Redis", _redis.GetConnectionString());
            builder.UseSetting("Nats:Url", _nats.GetConnectionString());
            builder.UseSetting("Tenancy:Migrate", "true");
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

        using var _ = Factory.CreateClient();
    }

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
