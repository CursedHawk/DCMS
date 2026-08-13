extern alias AdminApiApp;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Dcms.IntegrationTests.Tenancy;
using NATS.Client.JetStream;
using NATS.Client.JetStream.Models;
using NATS.Net;
using Testcontainers.PostgreSql;
using Testcontainers.Redis;
using Testcontainers.Nats;

namespace Dcms.IntegrationTests.Cms;

/// <summary>
/// Boots admin-api (authoring) AND content-api (delivery) against shared
/// Postgres + Redis + NATS containers, exercising the full publish → outbox →
/// delivery vertical. admin-api applies migrations; content-api reads.
/// </summary>
public sealed class ContentFlowFixture : IAsyncLifetime
{
    private readonly PostgreSqlContainer _postgres = TestPostgres.Build();

    private readonly RedisContainer _redis = new RedisBuilder("redis:7").Build();
    private readonly NatsContainer _nats = new NatsBuilder("nats:2.11").WithCommand("--jetstream").Build();

    public WebApplicationFactory<AdminApiApp::Program> Admin { get; private set; } = null!;
    public WebApplicationFactory<Program> Content { get; private set; } = null!;

    public async ValueTask InitializeAsync()
    {
        await Task.WhenAll(_postgres.StartAsync(), _redis.StartAsync(), _nats.StartAsync());

        await using (var nats = new NatsClient(_nats.GetConnectionString()))
        {
            var js = nats.CreateJetStreamContext();
            await js.CreateStreamAsync(new StreamConfig("TENANCY", ["tenant.>", "plugin.instance.>", "membership.>"]));
            await js.CreateStreamAsync(new StreamConfig("CMS", ["content.>"]));
        }

        Admin = new WebApplicationFactory<AdminApiApp::Program>().WithWebHostBuilder(b =>
        {
            ApplySettings(b);
            b.UseSetting("Tenancy:Migrate", "true");
            b.ConfigureTestServices(services => services
                .AddAuthentication(TestAuthHandler.SchemeName)
                .AddScheme<AuthenticationSchemeOptions, TestAuthHandler>(TestAuthHandler.SchemeName, _ => { }));
        });

        // Force admin host start (migrations) before content reads.
        using (Admin.CreateClient()) { }

        Content = new WebApplicationFactory<Program>().WithWebHostBuilder(ApplySettings);
        using (Content.CreateClient()) { }
    }

    private void ApplySettings(IWebHostBuilder b)
    {
        b.UseSetting("ConnectionStrings:Postgres", _postgres.GetConnectionString());
        b.UseSetting("ConnectionStrings:Redis", _redis.GetConnectionString());
        b.UseSetting("Nats:Url", _nats.GetConnectionString());
    }

    public async ValueTask DisposeAsync()
    {
        if (Admin is not null) await Admin.DisposeAsync();
        if (Content is not null) await Content.DisposeAsync();
        await Task.WhenAll(_postgres.DisposeAsync().AsTask(), _redis.DisposeAsync().AsTask(), _nats.DisposeAsync().AsTask());
    }
}

[CollectionDefinition(Name)]
public sealed class ContentFlowCollection : ICollectionFixture<ContentFlowFixture>
{
    public const string Name = "content-flow";
}
