extern alias IdentityApp;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Dcms.Shared.Data.Audit;
using Dcms.Shared.Data.DataProtection;
using Microsoft.EntityFrameworkCore;
using Testcontainers.PostgreSql;

namespace Dcms.IntegrationTests.Identity;

/// <summary>
/// Boots the real identity service against a throwaway Postgres container. A first boot as the
/// owner applies migrations and seeds roles, the SuperAdmin and the OpenIddict clients/scopes, as
/// identity-migrate does; the service under test then runs as <c>dcms_identity</c>, as in a
/// deploy, so token flows are exercised end to end under the role production uses.
/// </summary>
public sealed class IdentityAppFixture : IAsyncLifetime
{
    private readonly PostgreSqlContainer _postgres = TestPostgres.Build();

    public WebApplicationFactory<IdentityApp::Program> Factory { get; private set; } = null!;

    public const string Issuer = "http://identity.test/";

    /// <summary>The owner's connection, for tests that inspect what the service wrote.</summary>
    public string OwnerConnectionString => _postgres.GetConnectionString();

    public async ValueTask InitializeAsync()
    {
        await _postgres.StartAsync();
        var owner = _postgres.GetConnectionString();

        // What admin-api's migrate job creates in a deploy and identity then writes to: the
        // audit outbox and the shared key ring. Without them the grants below would name
        // tables that do not exist, and the run would not be the one production makes.
        await using (var audit = new AuditDbContext(new DbContextOptionsBuilder<AuditDbContext>().UseNpgsql(owner).Options))
        {
            await audit.Database.MigrateAsync();
        }
        await using (var keys = new DataProtectionDbContext(new DbContextOptionsBuilder<DataProtectionDbContext>().UseNpgsql(owner).Options))
        {
            await keys.Database.MigrateAsync();
        }

        // identity-migrate, as the owner: migrations and seeding. Then the service itself runs as
        // dcms_identity, the way it does in a deploy since ADR 0015 phase 5.
        await using (var migrate = Create(owner, migrateAndSeed: true))
        {
            using var _ = migrate.CreateClient();
        }
        await AppRole.GrantIdentityAsync(owner);

        Factory = Create(AppRole.IdentityConnectionString(owner), migrateAndSeed: false);
        using var client = Factory.CreateClient();
    }

    private static WebApplicationFactory<IdentityApp::Program> Create(string postgres, bool migrateAndSeed) =>
        new WebApplicationFactory<IdentityApp::Program>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("ConnectionStrings:Postgres", postgres);
            builder.UseSetting("Identity:Issuer", Issuer);
            builder.UseSetting("Identity:AllowInsecureHttp", "true");
            builder.UseSetting("Identity:Migrate", migrateAndSeed ? "true" : "false");
            builder.UseSetting("Identity:Seed", migrateAndSeed ? "true" : "false");
            builder.ConfigureAppConfiguration((_, config) =>
                config.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Nats:Url"] = "nats://localhost:4222",
                }));
        });

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
