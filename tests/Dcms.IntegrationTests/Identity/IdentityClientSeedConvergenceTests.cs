extern alias IdentityApp;

using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using OpenIddict.Abstractions;
using Testcontainers.PostgreSql;

namespace Dcms.IntegrationTests.Identity;

/// <summary>
/// The seeder must be able to REPAIR an OIDC client, not only create one.
///
/// <para>Both SPA clients used to be seeded create-if-absent, so a client first registered on a
/// host whose configuration was wrong stayed wrong for the life of that database. That is not
/// hypothetical: the platform console's redirect URIs were set on the <c>identity</c> service
/// but not on <c>identity-migrate</c> — the job that actually runs this seeder — so the client
/// was registered against <c>localhost</c>, and every sign-in was refused with
/// "The specified 'redirect_uri' is not valid for this client application". No redeploy could
/// fix it, because seeding skipped a client that already existed.</para>
///
/// <para>This boots the real identity service twice against one database, with different
/// redirect URIs, and asserts the second boot wins. It carries its own Postgres rather than
/// joining the shared fixture because the whole point is the second startup.</para>
/// </summary>
public sealed class IdentityClientSeedConvergenceTests : IAsyncLifetime
{
    private readonly PostgreSqlContainer _postgres = TestPostgres.Build();

    public ValueTask InitializeAsync() => new(_postgres.StartAsync());

    public ValueTask DisposeAsync() => _postgres.DisposeAsync();

    [DockerFact]
    public async Task Reseeding_moves_an_existing_client_onto_the_configured_redirect_uris()
    {
        var ct = TestContext.Current.CancellationToken;

        // First boot: the mis-configured host. This is exactly what vps1 had.
        await using (var wrong = Boot("http://localhost:5174/auth/callback", "http://localhost:5174/"))
        {
            (await RedirectUrisAsync(wrong, ct))
                .Should().BeEquivalentTo(["http://localhost:5174/auth/callback"]);
        }

        // Second boot: same database, corrected configuration — i.e. the next deploy.
        await using var fixedUp = Boot("https://platform.example.test/auth/callback", "https://platform.example.test/");

        (await RedirectUrisAsync(fixedUp, ct))
            .Should().BeEquivalentTo(["https://platform.example.test/auth/callback"]);
        (await PostLogoutUrisAsync(fixedUp, ct))
            .Should().BeEquivalentTo(["https://platform.example.test/"]);
    }

    private WebApplicationFactory<IdentityApp::Program> Boot(string redirect, string postLogout)
    {
        var factory = new WebApplicationFactory<IdentityApp::Program>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("ConnectionStrings:Postgres", _postgres.GetConnectionString());
            builder.UseSetting("Identity:Issuer", IdentityAppFixture.Issuer);
            builder.UseSetting("Identity:AllowInsecureHttp", "true");
            builder.UseSetting("Identity:Migrate", "true");
            builder.UseSetting("Identity:Seed", "true");
            builder.UseSetting("Identity:PlatformSpa:RedirectUris", redirect);
            builder.UseSetting("Identity:PlatformSpa:PostLogoutUris", postLogout);
            builder.ConfigureAppConfiguration((_, config) =>
                config.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Nats:Url"] = "nats://localhost:4222",
                }));
        });

        // Forces host start, which runs the seeder hosted service.
        factory.CreateClient().Dispose();
        return factory;
    }

    private static async Task<IReadOnlyList<string>> RedirectUrisAsync(
        WebApplicationFactory<IdentityApp::Program> factory, CancellationToken ct) =>
        (await DescribeAsync(factory, ct)).RedirectUris.Select(u => u.ToString()).ToList();

    private static async Task<IReadOnlyList<string>> PostLogoutUrisAsync(
        WebApplicationFactory<IdentityApp::Program> factory, CancellationToken ct) =>
        (await DescribeAsync(factory, ct)).PostLogoutRedirectUris.Select(u => u.ToString()).ToList();

    private static async Task<OpenIddictApplicationDescriptor> DescribeAsync(
        WebApplicationFactory<IdentityApp::Program> factory, CancellationToken ct)
    {
        using var scope = factory.Services.CreateScope();
        var manager = scope.ServiceProvider.GetRequiredService<IOpenIddictApplicationManager>();
        var application = await manager.FindByClientIdAsync("dcms-platform-spa", ct);
        application.Should().NotBeNull("the seeder must register the platform console client");

        var descriptor = new OpenIddictApplicationDescriptor();
        await manager.PopulateAsync(descriptor, application!, ct);
        return descriptor;
    }
}
