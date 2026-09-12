extern alias AdminApiApp;
extern alias SiteBuilderApp;
extern alias SiteHostApp;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Dcms.IntegrationTests.Tenancy;
using Minio;
using Minio.DataModel.Args;
using NATS.Client.JetStream.Models;
using NATS.Net;
using Testcontainers.Minio;
using Testcontainers.Nats;
using Testcontainers.PostgreSql;
using Testcontainers.Redis;

namespace Dcms.IntegrationTests.Sites;

/// <summary>
/// Boots admin-api (authoring), site-builder (render) and site-host (serving)
/// against shared Postgres + MinIO + NATS, exercising the publish → render →
/// serve-by-domain vertical.
/// </summary>
public sealed class SitePublishFixture : IAsyncLifetime
{
    private readonly PostgreSqlContainer _postgres = TestPostgres.Build();
    private readonly NatsContainer _nats = new NatsBuilder("nats:2.11").WithCommand("--jetstream").Build();
    private readonly MinioContainer _minio = TestMinio.Build();

    // admin-api's permission checks read through the Redis cache, so this needs to
    // be a real server: an unreachable address surfaces as a 500, not a cache miss.
    private readonly RedisContainer _redis = new RedisBuilder("redis:7").Build();

    public WebApplicationFactory<AdminApiApp::Program> Admin { get; private set; } = null!;
    public WebApplicationFactory<SiteHostApp::Program> Host { get; private set; } = null!;
    private WebApplicationFactory<SiteBuilderApp::Program> _builder = null!;

    public async ValueTask InitializeAsync()
    {
        await Task.WhenAll(_postgres.StartAsync(), _nats.StartAsync(), _minio.StartAsync(), _redis.StartAsync());

        await using (var nats = new NatsClient(_nats.GetConnectionString()))
        {
            var js = nats.CreateJetStreamContext();
            await js.CreateStreamAsync(new StreamConfig("TENANCY", ["tenant.>", "plugin.instance.>", "membership.>"]));
            await js.CreateStreamAsync(new StreamConfig("CMS", ["content.>"]));
            await js.CreateStreamAsync(new StreamConfig("SITES", ["site.publish.>"]));
            await js.CreateStreamAsync(new StreamConfig("SITES_EVENTS", ["site.published", "site.build.failed"]));
        }

        var endpoint = $"{_minio.Hostname}:{_minio.GetMappedPublicPort(9000)}";
        var minio = new MinioClient().WithEndpoint(endpoint)
            .WithCredentials(_minio.GetAccessKey(), _minio.GetSecretKey()).WithSSL(false).Build();
        await minio.MakeBucketAsync(new MakeBucketArgs().WithBucket("dcms-sites"));

        void Storage(IWebHostBuilder b)
        {
            b.UseSetting("ConnectionStrings:Postgres", _postgres.GetConnectionString());
            b.UseSetting("Nats:Url", _nats.GetConnectionString());
            b.UseSetting("ConnectionStrings:Redis", _redis.GetConnectionString());
            b.UseSetting("Storage:Endpoint", endpoint);
            b.UseSetting("Storage:AccessKey", _minio.GetAccessKey());
            b.UseSetting("Storage:SecretKey", _minio.GetSecretKey());
            b.UseSetting("Storage:UseSsl", "false");
        }

        Admin = new WebApplicationFactory<AdminApiApp::Program>().WithWebHostBuilder(b =>
        {
            Storage(b);
            b.UseSetting("Tenancy:Migrate", "true");
            b.UseSetting("Domains:AutoVerify", "true");
            b.ConfigureTestServices(s => s.AddAuthentication(TestAuthHandler.SchemeName)
                .AddScheme<AuthenticationSchemeOptions, TestAuthHandler>(TestAuthHandler.SchemeName, _ => { }));
        });
        using (Admin.CreateClient()) { } // migrate first

        _builder = new WebApplicationFactory<SiteBuilderApp::Program>().WithWebHostBuilder(Storage);
        using (_builder.CreateClient()) { }

        Host = new WebApplicationFactory<SiteHostApp::Program>().WithWebHostBuilder(Storage);
        using (Host.CreateClient()) { }
    }

    public async ValueTask DisposeAsync()
    {
        if (Admin is not null) await Admin.DisposeAsync();
        if (_builder is not null) await _builder.DisposeAsync();
        if (Host is not null) await Host.DisposeAsync();
        await Task.WhenAll(_postgres.DisposeAsync().AsTask(), _nats.DisposeAsync().AsTask(),
            _minio.DisposeAsync().AsTask(), _redis.DisposeAsync().AsTask());
    }
}

[CollectionDefinition(Name)]
public sealed class SitePublishCollection : ICollectionFixture<SitePublishFixture>
{
    public const string Name = "site-publish";
}
