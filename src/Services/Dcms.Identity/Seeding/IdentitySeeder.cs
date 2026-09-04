using Dcms.Identity.Data;
using Dcms.Identity.Domain;
using Dcms.Shared.Data;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using OpenIddict.Abstractions;
using static OpenIddict.Abstractions.OpenIddictConstants;

namespace Dcms.Identity.Seeding;

/// <summary>
/// Idempotent migration and seeding: applies the identity migrations, then creates global roles,
/// the SuperAdmin user, OpenIddict scopes and the SPA + service clients.
///
/// <para><b>Intended to run as a one-shot job.</b> <c>identity --migrate-only</c> runs it and
/// exits; the deploy pipeline does that before rolling any service, and services then start with
/// <c>Identity:Migrate=false</c> and <c>Identity:Seed=false</c>.</para>
///
/// <para>The advisory lock is what makes the startup path safe until every environment is on the
/// job. The seed steps are individually idempotent (each does a <c>FindBy...</c> first), but
/// idempotent is not the same as concurrency-safe: two instances checking "does this role exist"
/// at the same time both see no, and both create it. Serialising them makes the check mean what
/// it looks like it means.</para>
/// </summary>
public sealed class IdentitySeeder(
    IServiceProvider services,
    IConfiguration configuration,
    ILogger<IdentitySeeder> logger) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken) => ExecuteAsync(
        configuration.GetValue("Identity:Migrate", true),
        configuration.GetValue("Identity:Seed", true),
        cancellationToken);

    /// <summary>
    /// Runs migration and/or seeding under the identity advisory lock. Called by the hosted
    /// service with the configured flags, and by <c>--migrate-only</c> with both forced on --
    /// that flag means "do not do this at startup", and a migration job is not a startup.
    /// </summary>
    public async Task ExecuteAsync(bool migrate, bool seed, CancellationToken cancellationToken)
    {
        if (!migrate && !seed)
        {
            logger.LogInformation(
                "Identity:Migrate and Identity:Seed are both false; expecting the migration job to have run.");
            return;
        }

        var connectionString = configuration.GetConnectionString("Postgres")
                               ?? throw new InvalidOperationException(
                                   "ConnectionStrings:Postgres is required to migrate or seed identity.");

        await using var identityLock = await PostgresAdvisoryLock.AcquireAsync(
            connectionString, PostgresAdvisoryLock.IdentityLockKey, logger, cancellationToken);

        using var scope = services.CreateScope();
        var sp = scope.ServiceProvider;

        if (migrate)
        {
            var db = sp.GetRequiredService<IdentityDbContext>();
            await db.Database.MigrateAsync(cancellationToken);
            logger.LogInformation("Identity database migrated.");
        }

        if (!seed)
        {
            return;
        }

        await SeedRolesAsync(sp);
        await SeedSuperAdminAsync(sp);
        await SeedScopesAsync(sp, cancellationToken);
        await SeedClientsAsync(sp, cancellationToken);
        logger.LogInformation("Identity seeding complete.");
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    private static async Task SeedRolesAsync(IServiceProvider sp)
    {
        var roleManager = sp.GetRequiredService<RoleManager<DcmsRole>>();
        foreach (var role in GlobalRoles.All)
        {
            if (!await roleManager.RoleExistsAsync(role))
            {
                await roleManager.CreateAsync(new DcmsRole(role));
            }
        }
    }

    private async Task SeedSuperAdminAsync(IServiceProvider sp)
    {
        var userManager = sp.GetRequiredService<UserManager<DcmsUser>>();
        var email = configuration["Identity:SuperAdmin:Email"] ?? "admin@dcms.local";
        var password = configuration["Identity:SuperAdmin:Password"] ?? "Admin!23456";

        if (await userManager.FindByEmailAsync(email) is not null)
        {
            return;
        }

        var user = new DcmsUser
        {
            UserName = email,
            Email = email,
            EmailConfirmed = true,
            DisplayName = "Platform Administrator",
        };
        var result = await userManager.CreateAsync(user, password);
        if (!result.Succeeded)
        {
            throw new InvalidOperationException(
                "Failed to seed SuperAdmin: " + string.Join("; ", result.Errors.Select(e => e.Description)));
        }
        await userManager.AddToRoleAsync(user, GlobalRoles.SuperAdmin);

        // Mirror into Forgejo so the platform admin can use git with these credentials.
        await sp.GetRequiredService<Forgejo.ForgejoUserSync>().EnsureAsync(user, password, CancellationToken.None);

        logger.LogInformation("Seeded SuperAdmin {Email}.", email);
    }

    private static async Task SeedScopesAsync(IServiceProvider sp, CancellationToken ct)
    {
        var manager = sp.GetRequiredService<IOpenIddictScopeManager>();

        await EnsureScopeAsync(manager, DcmsOAuth.Scopes.Admin, "DCMS Admin API", DcmsOAuth.Resources.AdminApi, ct);
        await EnsureScopeAsync(manager, DcmsOAuth.Scopes.Ai, "DCMS AI Gateway", DcmsOAuth.Resources.AiGateway, ct);
        // Same resource as dcms.admin -- it is admin-api that answers -- but a separate scope,
        // so the service token content-api carries is only good for the endpoints that named it.
        await EnsureScopeAsync(
            manager, DcmsOAuth.Scopes.Social, "DCMS Social (service)", DcmsOAuth.Resources.AdminApi, ct);
        await EnsureScopeAsync(
            manager, DcmsOAuth.Scopes.Platform, "DCMS Platform Console", DcmsOAuth.Resources.PlatformApi, ct);
    }

    private static async Task EnsureScopeAsync(
        IOpenIddictScopeManager manager, string name, string displayName, string resource, CancellationToken ct)
    {
        if (await manager.FindByNameAsync(name, ct) is not null)
        {
            return;
        }

        await manager.CreateAsync(new OpenIddictScopeDescriptor
        {
            Name = name,
            DisplayName = displayName,
            Resources = { resource },
        }, ct);
    }

    private async Task SeedClientsAsync(IServiceProvider sp, CancellationToken ct)
    {
        var manager = sp.GetRequiredService<IOpenIddictApplicationManager>();

        var spaRedirects = (configuration["Identity:Spa:RedirectUris"]
                            ?? "http://localhost:5173/auth/callback;http://localhost:5000/auth/callback")
            .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var spaPostLogout = (configuration["Identity:Spa:PostLogoutUris"]
                            ?? "http://localhost:5173/;http://localhost:5000/")
            .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        await EnsurePublicSpaClientAsync(
            manager,
            DcmsOAuth.Clients.AdminSpa,
            "DCMS Admin SPA",
            [DcmsOAuth.Scopes.Admin],
            spaRedirects,
            spaPostLogout,
            ct);

        // The platform console. A separate public client rather than another redirect URI on
        // the admin SPA: the two are served from different hosts, and a shared client would let
        // a token minted for admin.highgeek.eu be replayed into platform.highgeek.eu's callback.
        //
        // It asks for dcms.admin as well as dcms.platform because the console calls three APIs
        // same-origin through Caddy — platform-api for observability and ops, identity for the
        // user directory, and admin-api for tenancy and audit, which already own those. Nothing
        // here grants anything: every one of those endpoints is gated on SuperAdmin or a
        // platform permission on the server side.
        var platformRedirects = (configuration["Identity:PlatformSpa:RedirectUris"]
                                 ?? "http://localhost:5174/auth/callback;http://localhost:5010/auth/callback")
            .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var platformPostLogout = (configuration["Identity:PlatformSpa:PostLogoutUris"]
                                  ?? "http://localhost:5174/;http://localhost:5010/")
            .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        await EnsurePublicSpaClientAsync(
            manager,
            DcmsOAuth.Clients.PlatformSpa,
            "DCMS Platform Console",
            [DcmsOAuth.Scopes.Platform, DcmsOAuth.Scopes.Admin],
            platformRedirects,
            platformPostLogout,
            ct);

        if (await manager.FindByClientIdAsync(DcmsOAuth.Clients.AdminApiService, ct) is null)
        {
            var secret = configuration["Identity:AdminApiService:Secret"] ?? "dcms-admin-api-dev-secret";
            await manager.CreateAsync(new OpenIddictApplicationDescriptor
            {
                ClientId = DcmsOAuth.Clients.AdminApiService,
                ClientSecret = secret,
                ClientType = ClientTypes.Confidential,
                DisplayName = "DCMS Admin API (service)",
                Permissions =
                {
                    Permissions.Endpoints.Token,
                    Permissions.GrantTypes.ClientCredentials,
                    Permissions.Prefixes.Scope + DcmsOAuth.Scopes.Ai,
                    Permissions.Prefixes.Scope + DcmsOAuth.Scopes.Social,
                },
            }, ct);
            logger.LogInformation("Seeded admin-api service client.");
        }

        await SeedGrafanaClientAsync(manager, ct);
    }

    /// <summary>
    /// The Grafana OIDC client. Confidential + PKCE: Grafana keeps the secret server-side, and
    /// PKCE costs nothing on top of that while removing the authorization-code interception
    /// class of attack entirely.
    ///
    /// <para>The <c>roles</c> scope is what makes the whole arrangement safe. Grafana maps
    /// <c>contains(roles[*], 'SuperAdmin')</c> to Grafana Admin with
    /// <c>role_attribute_strict</c>, so a user without it is refused rather than being given
    /// the Viewer role — and a Viewer on these dashboards can read every tenant's usage, every
    /// audit action and every trace on the platform. Drop the scope and every DCMS user with a
    /// login becomes a platform-wide observer.</para>
    ///
    /// <para>Skipped entirely when no secret is configured, rather than seeded with a default.
    /// A client with a guessable secret and this role mapping is worse than no Grafana login:
    /// the break-glass local admin still works, so the failure mode of skipping is an
    /// inconvenience, and the failure mode of a default secret is a platform-wide read.</para>
    /// </summary>
    private async Task SeedGrafanaClientAsync(IOpenIddictApplicationManager manager, CancellationToken ct)
    {
        if (await manager.FindByClientIdAsync(DcmsOAuth.Clients.Grafana, ct) is not null)
        {
            return;
        }

        var secret = configuration["Identity:Grafana:Secret"];
        if (string.IsNullOrWhiteSpace(secret))
        {
            logger.LogInformation(
                "Grafana OIDC client not seeded: Identity:Grafana:Secret is unset. " +
                "Set it (GRAFANA_OIDC_CLIENT_SECRET) to enable Grafana SSO.");
            return;
        }

        var redirects = (configuration["Identity:Grafana:RedirectUris"]
                         ?? "https://grafana.highgeek.eu/login/generic_oauth")
            .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        var descriptor = new OpenIddictApplicationDescriptor
        {
            ClientId = DcmsOAuth.Clients.Grafana,
            ClientSecret = secret,
            ClientType = ClientTypes.Confidential,
            ConsentType = ConsentTypes.Implicit,
            DisplayName = "DCMS Grafana",
            Permissions =
            {
                Permissions.Endpoints.Authorization,
                Permissions.Endpoints.Token,
                Permissions.Endpoints.EndSession,
                Permissions.GrantTypes.AuthorizationCode,
                Permissions.GrantTypes.RefreshToken,
                Permissions.ResponseTypes.Code,
                Permissions.Scopes.Email,
                Permissions.Scopes.Profile,
                Permissions.Scopes.Roles,
            },
            Requirements = { Requirements.Features.ProofKeyForCodeExchange },
        };
        foreach (var uri in redirects)
        {
            descriptor.RedirectUris.Add(new Uri(uri));
        }

        await manager.CreateAsync(descriptor, ct);
        logger.LogInformation("Seeded Grafana OIDC client.");
    }

    /// <summary>
    /// Creates a public code+PKCE client, or brings an existing one's URIs back in line with
    /// configuration.
    ///
    /// <para><b>Why this converges rather than only creating.</b> Both SPA clients used to be
    /// seeded once and never touched again, so a client first created on a host whose
    /// configuration was wrong stayed wrong for the life of that database — no redeploy could
    /// repair it, and the only symptom is OpenIddict refusing the sign-in with
    /// "The specified 'redirect_uri' is not valid for this client application". That is exactly
    /// what happened to the platform console: the seeder runs in the identity-migrate job, and
    /// that job had not been given the public redirect URIs, so the client was registered
    /// against localhost.</para>
    ///
    /// <para>The redirect URIs come from configuration and from nowhere else, so configuration
    /// is allowed to be the authority on them. Everything else about an existing client is left
    /// alone.</para>
    /// </summary>
    private async Task EnsurePublicSpaClientAsync(
        IOpenIddictApplicationManager manager,
        string clientId,
        string displayName,
        IReadOnlyList<string> resourceScopes,
        IReadOnlyList<string> redirectUris,
        IReadOnlyList<string> postLogoutUris,
        CancellationToken ct)
    {
        var descriptor = new OpenIddictApplicationDescriptor
        {
            ClientId = clientId,
            ClientType = ClientTypes.Public,
            ConsentType = ConsentTypes.Implicit,
            DisplayName = displayName,
            Permissions =
            {
                Permissions.Endpoints.Authorization,
                Permissions.Endpoints.Token,
                Permissions.Endpoints.EndSession,
                Permissions.GrantTypes.AuthorizationCode,
                Permissions.GrantTypes.RefreshToken,
                Permissions.ResponseTypes.Code,
                Permissions.Scopes.Email,
                Permissions.Scopes.Profile,
                Permissions.Scopes.Roles,
            },
            Requirements = { Requirements.Features.ProofKeyForCodeExchange },
        };

        foreach (var scope in resourceScopes)
        {
            descriptor.Permissions.Add(Permissions.Prefixes.Scope + scope);
        }
        foreach (var uri in redirectUris)
        {
            descriptor.RedirectUris.Add(new Uri(uri));
        }
        foreach (var uri in postLogoutUris)
        {
            descriptor.PostLogoutRedirectUris.Add(new Uri(uri));
        }

        var existing = await manager.FindByClientIdAsync(clientId, ct);
        if (existing is null)
        {
            await manager.CreateAsync(descriptor, ct);
            logger.LogInformation("Seeded {Client} with {Count} redirect URI(s).", clientId, redirectUris.Count);
            return;
        }

        var current = new OpenIddictApplicationDescriptor();
        await manager.PopulateAsync(current, existing, ct);

        if (current.RedirectUris.SetEquals(descriptor.RedirectUris)
            && current.PostLogoutRedirectUris.SetEquals(descriptor.PostLogoutRedirectUris))
        {
            return;
        }

        // Only the URIs. Anything else an operator changed on the client stays as they left it.
        var before = string.Join(", ", current.RedirectUris.Select(u => u.ToString()));
        current.RedirectUris.Clear();
        current.PostLogoutRedirectUris.Clear();
        foreach (var uri in descriptor.RedirectUris) current.RedirectUris.Add(uri);
        foreach (var uri in descriptor.PostLogoutRedirectUris) current.PostLogoutRedirectUris.Add(uri);

        await manager.PopulateAsync(existing, current, ct);
        await manager.UpdateAsync(existing, ct);

        logger.LogInformation(
            "Updated {Client} redirect URIs from configuration. Was: {Before}. Now: {After}.",
            clientId,
            string.IsNullOrEmpty(before) ? "(none)" : before,
            string.Join(", ", descriptor.RedirectUris.Select(u => u.ToString())));
    }

}
