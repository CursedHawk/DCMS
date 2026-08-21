extern alias AdminApiApp;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using NATS.Client.JetStream;
using NATS.Client.JetStream.Models;
using NATS.Net;
using Testcontainers.PostgreSql;
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

    public WebApplicationFactory<AdminApiApp::Program> Factory { get; private set; } = null!;

    /// <summary>Raw Postgres connection string (as the owner) — used by the RLS isolation test.</summary>
    public string PostgresConnectionString => _postgres.GetConnectionString();

    public async ValueTask InitializeAsync()
    {
        await Task.WhenAll(_postgres.StartAsync(), _redis.StartAsync(), _nats.StartAsync());

        await using (var nats = new NatsClient(_nats.GetConnectionString()))
        {
            var js = nats.CreateJetStreamContext();
            await js.CreateStreamAsync(new StreamConfig("TENANCY", ["tenant.>", "plugin.instance.>", "membership.>"]));
            await js.CreateStreamAsync(new StreamConfig("CMS", ["content.>"]));
            // The chain writer fans every appended record out to this after it commits. Not
            // needed for the record itself — that path is a row in the same transaction — but
            // without the stream the fan-out logs an error on every test that changes anything.
            await js.CreateStreamAsync(new StreamConfig("AUDIT", ["audit.>"]));
        }

        Factory = new WebApplicationFactory<AdminApiApp::Program>().WithWebHostBuilder(builder =>
        {
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
        await Task.WhenAll(_postgres.DisposeAsync().AsTask(), _redis.DisposeAsync().AsTask(), _nats.DisposeAsync().AsTask());
    }
}

[CollectionDefinition(Name)]
public sealed class AdminApiCollection : ICollectionFixture<AdminApiFixture>
{
    public const string Name = "admin-api";
}
