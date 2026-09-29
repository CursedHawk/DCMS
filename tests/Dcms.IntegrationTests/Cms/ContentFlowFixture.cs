extern alias AdminApiApp;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Dcms.IntegrationTests.Social;
using Dcms.IntegrationTests.Tenancy;
using Dcms.Shared.Security;
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

    /// <summary>In-process stand-in for the Meta Graph API. Inert unless a test drives it.</summary>
    public MetaStubServer MetaStub { get; private set; } = null!;

    /// <summary>
    /// The scope content-api presents to admin-api. Mutable so a test can prove the guard
    /// actually refuses the wrong one — a scope check nothing has ever failed is decoration.
    /// </summary>
    public string ServiceScope { get; set; } = "dcms.social";

    /// <summary>The fixture's NATS server, for tests that read a stream directly.</summary>
    public string NatsUrl => _nats.GetConnectionString();

    /// <summary>Raw Postgres connection string, as the owner — for seeding rows a test needs directly.</summary>
    public string PostgresConnectionString => _postgres.GetConnectionString();

    public async ValueTask InitializeAsync()
    {
        await Task.WhenAll(_postgres.StartAsync(), _redis.StartAsync(), _nats.StartAsync());

        await using (var nats = new NatsClient(_nats.GetConnectionString()))
        {
            var js = nats.CreateJetStreamContext();
            await js.CreateStreamAsync(new StreamConfig("TENANCY", ["tenant.>", "plugin.instance.>", "membership.>"]));
            await js.CreateStreamAsync(new StreamConfig("CMS", ["content.>"]));
            await js.CreateStreamAsync(new StreamConfig("ANALYTICS", ["analytics.>"]));
            // Plugin events (visitor.registered, form.submitted) and the admin-api job consumer.
            await js.CreateStreamAsync(new StreamConfig("PLUGIN_EVENTS", ["plugins.events.>"]));
            await js.CreateStreamAsync(new StreamConfig("PLUGIN_JOBS", ["plugins.jobs.>"]) { Retention = StreamConfigRetention.Workqueue });
        }

        MetaStub = await MetaStubServer.StartAsync();

        Admin = new WebApplicationFactory<AdminApiApp::Program>().WithWebHostBuilder(b =>
        {
            ApplySettings(b);
            b.UseSetting("Tenancy:Migrate", "true");
            // Enough of a Meta app to get past the "not configured" 501; every call goes to
            // the stub. The sync timer stays off — these tests are about the live path.
            b.UseSetting("Social:Meta:AppId", "test-fb-app");
            b.UseSetting("Social:Meta:AppSecret", "test-fb-secret");
            b.UseSetting("Social:RedirectUri", "https://admin.test/api/admin/social/callback");
            b.UseSetting("Social:OverrideBaseUrl", MetaStub.BaseUrl);
            b.UseSetting("Social:SyncEnabled", "false");
            b.ConfigureTestServices(services =>
            {
                services.AddAuthentication(TestAuthHandler.SchemeName)
                    .AddScheme<AuthenticationSchemeOptions, TestAuthHandler>(TestAuthHandler.SchemeName, _ => { });
                services.AddSingleton<Dcms.Shared.Vault.ITransitEncryptor, FakeTransitEncryptor>();
            });
        });

        // Force admin host start (migrations) before content reads.
        using (Admin.CreateClient()) { }

        // ADR 0015 phase 4: content-api runs as it does in a deploy, on dcms_app (NOBYPASSRLS)
        // with the tenant GUC interceptor on. admin-api above stays the owner, as the migrate
        // job is.
        await AppRole.GrantAsync(_postgres.GetConnectionString());

        Content = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            ApplySettings(b);
            b.UseSetting("ConnectionStrings:Postgres", AppRole.ConnectionString(_postgres.GetConnectionString()));
            b.UseSetting("Rls:Enforce", "true");
            b.ConfigureTestServices(services =>
            {
                // There is no identity server in this fixture, and minting real OIDC tokens
                // would test OpenIddict rather than the stories path.
                services.AddSingleton<IServiceTokenProvider>(new StubServiceTokenProvider());

                // Point content-api's admin-api client at the in-memory admin host, translating
                // the bearer into the test scheme's headers. The scope travels for real, so
                // ServicePrincipalGuard is genuinely in the path rather than bypassed.
                services.AddHttpClient(Dcms.ContentApi.Social.StoryDeliveryEndpoints.HttpClientName)
                    .ConfigurePrimaryHttpMessageHandler(() => new ServicePrincipalHandler(this))
                    .ConfigureHttpClient(client => client.BaseAddress = new Uri("http://admin.test"));
            });
        });
        using (Content.CreateClient()) { }
    }

    private void ApplySettings(IWebHostBuilder b)
    {
        b.UseSetting("ConnectionStrings:Postgres", _postgres.GetConnectionString());
        b.UseSetting("ConnectionStrings:Redis", _redis.GetConnectionString());
        b.UseSetting("Nats:Url", _nats.GetConnectionString());
    }

    /// <summary>Returns whatever it is asked for: the value is never validated in this fixture.</summary>
    private sealed class StubServiceTokenProvider : IServiceTokenProvider
    {
        public Task<string> GetTokenAsync(string scope, CancellationToken ct = default) =>
            Task.FromResult($"stub-{scope}");
    }

    /// <summary>
    /// Sends content-api's request into the admin test server as a client-credentials caller:
    /// a non-guid subject (so it is a service principal, not a user) plus the granted scope.
    /// </summary>
    private sealed class ServicePrincipalHandler(ContentFlowFixture fixture) : DelegatingHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken ct)
        {
            InnerHandler ??= fixture.Admin.Server.CreateHandler();
            request.Headers.Remove("X-Test-Sub");
            request.Headers.Add("X-Test-Sub", "dcms-admin-api");
            request.Headers.Add("X-Test-Scope", fixture.ServiceScope);
            return base.SendAsync(request, ct);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (MetaStub is not null) await MetaStub.DisposeAsync();
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
