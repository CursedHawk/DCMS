extern alias IdentityApp;
using System.Net;
using System.Net.Http.Json;
using Dcms.IntegrationTests.Tenancy;
using IdentityApp::Dcms.Identity.Domain;
using IdentityApp::Dcms.Identity.Endpoints;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using OpenIddict.Abstractions;
using Testcontainers.PostgreSql;
using static OpenIddict.Abstractions.OpenIddictConstants;

namespace Dcms.IntegrationTests.Identity;

/// <summary>
/// SEC-05: locking an account or changing its password must END the live sessions, not just the
/// next sign-in. Before the fix a locked account kept minting access tokens off its refresh token
/// for the full 14-day lifetime, and a password change left a stolen refresh token working.
///
/// <para>Drives the real endpoints. Only the bearer check is swapped for the header-driven test
/// scheme — there is no password grant to mint a real SuperAdmin token with, and the policy is not
/// what this finding is about. It gets its own host so that swap cannot leak into the identity
/// tests that exercise real tokens.</para>
/// </summary>
public sealed class SessionRevocationTests : IAsyncLifetime
{
    private readonly PostgreSqlContainer _postgres = TestPostgres.Build();
    private WebApplicationFactory<IdentityApp::Program> _factory = null!;

    public async ValueTask InitializeAsync()
    {
        await _postgres.StartAsync();
        _factory = new WebApplicationFactory<IdentityApp::Program>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("ConnectionStrings:Postgres", _postgres.GetConnectionString());
            builder.UseSetting("Identity:Issuer", IdentityAppFixture.Issuer);
            builder.UseSetting("Identity:AllowInsecureHttp", "true");
            builder.UseSetting("Identity:Migrate", "true");
            builder.UseSetting("Identity:Seed", "true");
            builder.ConfigureAppConfiguration((_, config) =>
                config.AddInMemoryCollection(new Dictionary<string, string?> { ["Nats:Url"] = "nats://localhost:4222" }));
            builder.ConfigureTestServices(services =>
            {
                services.AddAuthentication()
                    .AddScheme<AuthenticationSchemeOptions, TestAuthHandler>(TestAuthHandler.SchemeName, _ => { });
                // Re-registering a policy by name replaces it; these run after the app's own.
                services.AddAuthorization(options =>
                {
                    options.AddPolicy(PlatformUserEndpoints.PolicyName, p => p
                        .AddAuthenticationSchemes(TestAuthHandler.SchemeName)
                        .RequireAuthenticatedUser()
                        .RequireRole(GlobalRoles.SuperAdmin));
                    options.AddPolicy(AccountApiEndpoints.PolicyName, p => p
                        .AddAuthenticationSchemes(TestAuthHandler.SchemeName)
                        .RequireAuthenticatedUser());
                });
            });
        });
        using var _ = _factory.CreateClient();
    }

    [DockerFact]
    public async Task Locking_an_account_revokes_its_live_tokens_and_rotates_its_stamp()
    {
        var ct = TestContext.Current.CancellationToken;
        var (userId, stampBefore) = await CreateUserWithLiveSessionAsync(password: null, ct);

        var operatorId = Guid.NewGuid();
        var req = new HttpRequestMessage(HttpMethod.Post, $"/api/identity/users/{userId}/lock");
        req.Headers.Add("X-Test-Sub", operatorId.ToString());
        req.Headers.Add("X-Test-Roles", GlobalRoles.SuperAdmin);
        var res = await _factory.CreateClient().SendAsync(req, ct);
        res.StatusCode.Should().Be(HttpStatusCode.NoContent);

        await AssertSessionEndedAsync(userId, stampBefore, ct);
    }

    [DockerFact]
    public async Task Changing_the_password_revokes_tokens_minted_under_the_old_one()
    {
        var ct = TestContext.Current.CancellationToken;
        const string oldPassword = "Old-passw0rd-123";
        var (userId, stampBefore) = await CreateUserWithLiveSessionAsync(oldPassword, ct);

        var req = new HttpRequestMessage(HttpMethod.Post, "/account/api/password")
        {
            Content = JsonContent.Create(new { currentPassword = oldPassword, newPassword = "New-passw0rd-456" }),
        };
        req.Headers.Add("X-Test-Sub", userId);
        var res = await _factory.CreateClient().SendAsync(req, ct);
        res.StatusCode.Should().Be(HttpStatusCode.NoContent);

        await AssertSessionEndedAsync(userId, stampBefore, ct);
    }

    /// <summary>A user holding a valid authorization plus a valid access and refresh token —
    /// the state a signed-in (or compromised) account is in.</summary>
    private async Task<(string UserId, string? Stamp)> CreateUserWithLiveSessionAsync(string? password, CancellationToken ct)
    {
        using var scope = _factory.Services.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<DcmsUser>>();
        var authorizations = scope.ServiceProvider.GetRequiredService<IOpenIddictAuthorizationManager>();
        var tokens = scope.ServiceProvider.GetRequiredService<IOpenIddictTokenManager>();

        var email = $"{Guid.NewGuid():N}@dcms.test";
        var user = new DcmsUser { Id = Guid.NewGuid(), UserName = email, Email = email, EmailConfirmed = true };
        var created = password is null ? await users.CreateAsync(user) : await users.CreateAsync(user, password);
        created.Succeeded.Should().BeTrue(string.Join("; ", created.Errors.Select(e => e.Description)));

        var subject = user.Id.ToString();
        var now = DateTimeOffset.UtcNow;
        var authorization = await authorizations.CreateAsync(new OpenIddictAuthorizationDescriptor
        {
            Subject = subject, Status = Statuses.Valid, Type = AuthorizationTypes.Permanent, CreationDate = now,
        }, ct);
        var authorizationId = await authorizations.GetIdAsync(authorization, ct);

        foreach (var type in new[] { TokenTypeHints.AccessToken, TokenTypeHints.RefreshToken })
        {
            await tokens.CreateAsync(new OpenIddictTokenDescriptor
            {
                Subject = subject, Status = Statuses.Valid, Type = type, AuthorizationId = authorizationId,
                CreationDate = now, ExpirationDate = now.AddDays(14),
            }, ct);
        }

        return (subject, await users.GetSecurityStampAsync(user));
    }

    private async Task AssertSessionEndedAsync(string userId, string? stampBefore, CancellationToken ct)
    {
        using var scope = _factory.Services.CreateScope();
        var tokens = scope.ServiceProvider.GetRequiredService<IOpenIddictTokenManager>();
        var authorizations = scope.ServiceProvider.GetRequiredService<IOpenIddictAuthorizationManager>();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<DcmsUser>>();

        var tokenStatuses = new List<string?>();
        await foreach (var token in tokens.FindBySubjectAsync(userId, ct))
        {
            tokenStatuses.Add(await tokens.GetStatusAsync(token, ct));
        }
        // Both the access and the refresh token — the refresh token is the one that matters.
        tokenStatuses.Should().HaveCount(2).And.OnlyContain(s => s == Statuses.Revoked);

        var authorizationStatuses = new List<string?>();
        await foreach (var authorization in authorizations.FindBySubjectAsync(userId, ct))
        {
            authorizationStatuses.Add(await authorizations.GetStatusAsync(authorization, ct));
        }
        authorizationStatuses.Should().ContainSingle().Which.Should().Be(Statuses.Revoked);

        // The stamp rotates too, which is what ends the interactive cookie session.
        var user = await users.FindByIdAsync(userId);
        (await users.GetSecurityStampAsync(user!)).Should().NotBe(stampBefore);
    }

    public async ValueTask DisposeAsync()
    {
        if (_factory is not null)
        {
            await _factory.DisposeAsync();
        }
        await _postgres.DisposeAsync();
    }
}
