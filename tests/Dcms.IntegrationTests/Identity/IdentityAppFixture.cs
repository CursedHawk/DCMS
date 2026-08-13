extern alias IdentityApp;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Testcontainers.PostgreSql;

namespace Dcms.IntegrationTests.Identity;

/// <summary>
/// Boots the real identity service against a throwaway Postgres container. The
/// startup seeder applies migrations and seeds roles, the SuperAdmin and the
/// OpenIddict clients/scopes, so token flows can be exercised end to end.
/// </summary>
public sealed class IdentityAppFixture : IAsyncLifetime
{
    private readonly PostgreSqlContainer _postgres = TestPostgres.Build();

    public WebApplicationFactory<IdentityApp::Program> Factory { get; private set; } = null!;

    public const string Issuer = "http://identity.test/";

    public async ValueTask InitializeAsync()
    {
        await _postgres.StartAsync();

        Factory = new WebApplicationFactory<IdentityApp::Program>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("ConnectionStrings:Postgres", _postgres.GetConnectionString());
            builder.UseSetting("Identity:Issuer", Issuer);
            builder.UseSetting("Identity:AllowInsecureHttp", "true");
            builder.UseSetting("Identity:Migrate", "true");
            builder.UseSetting("Identity:Seed", "true");
            builder.ConfigureAppConfiguration((_, config) =>
                config.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Nats:Url"] = "nats://localhost:4222",
                }));
        });

        // Force host start (runs the seeder hosted service).
        using var client = Factory.CreateClient();
    }

    public async ValueTask DisposeAsync()
    {
        if (Factory is not null)
        {
            await Factory.DisposeAsync();
        }
        await _postgres.DisposeAsync();
    }
}

[CollectionDefinition(Name)]
public sealed class IdentityCollection : ICollectionFixture<IdentityAppFixture>
{
    public const string Name = "identity";
}
