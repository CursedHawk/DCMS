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
        // Same resource as dcms.admin -- admin-api answers -- and its own scope, so the token
        // platform-api carries is only good for the endpoints that named it.
        await EnsureScopeAsync(
            manager, DcmsOAuth.Scopes.Console, "DCMS Console (service)", DcmsOAuth.Resources.AdminApi, ct);
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
        // same-origin through the edge — platform-api for observability and ops, identity for the
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

        await EnsureServiceClientAsync(
            manager,
            DcmsOAuth.Clients.AdminApiService,
            "DCMS Admin API (service)",
            configuration["Identity:AdminApiService:Secret"] ?? "dcms-admin-api-dev-secret",
            [DcmsOAuth.Scopes.Ai, DcmsOAuth.Scopes.Social],
            ct);

        // platform-api → admin-api, for the console's certificates, notifications, tenant
        // lifecycle and analytics prune. Its own client, not a reuse of admin-api's: whoever
        // holds that secret also holds dcms.ai and dcms.social.
        await EnsureServiceClientAsync(
            manager,
            DcmsOAuth.Clients.PlatformApiService,
            "DCMS Platform API (service)",
            configuration["Identity:PlatformApiService:Secret"] ?? "dcms-platform-api-dev-secret",
            [DcmsOAuth.Scopes.Console],
            ct);

        // The edge, which is now the only thing between an operator and Grafana or Forgejo.
        await SeedEdgeClientAsync(manager, ct);

        // And the client it replaced. Deleted rather than left alone -- see the method.
        await RetireGrafanaClientAsync(manager, ct);
    }

    /// <summary>
    /// The edge's OIDC client. Confidential + PKCE, exactly like the Grafana client it replaces:
    /// the edge keeps the secret server-side, and PKCE costs nothing on top of that while
    /// removing the authorization-code interception class of attack entirely.
    ///
    /// <para>One client, several redirect URIs — one per host the edge gates. They are separate
    /// URIs rather than a wildcard because OpenIddict compares them exactly, which is the
    /// property that makes a stolen authorization code useless anywhere else.</para>
    ///
    /// <para>Skipped when no secret is configured, and the edge independently disables its own
    /// authentication in that case. The failure mode of skipping is that Grafana and Forgejo
    /// show their own login screens; the failure mode of seeding a default secret would be that
    /// anyone holding it can mint a session the edge asserts to both. Those are not close.</para>
    /// </summary>
    private async Task SeedEdgeClientAsync(IOpenIddictApplicationManager manager, CancellationToken ct)
    {
        var secret = configuration["Identity:Edge:Secret"];
        if (string.IsNullOrWhiteSpace(secret))
        {
            logger.LogInformation(
                "Edge OIDC client not seeded: Identity:Edge:Secret is unset. " +
                "Grafana and Forgejo will use their own sign-in until it is set.");
            return;
        }

        var redirects = (configuration["Identity:Edge:RedirectUris"]
                         ?? "https://grafana.highgeek.eu/.edge/signin-oidc;https://git.highgeek.eu/.edge/signin-oidc")
            .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var postLogout = (configuration["Identity:Edge:PostLogoutUris"]
                          ?? "https://grafana.highgeek.eu/.edge/signout-callback-oidc;https://git.highgeek.eu/.edge/signout-callback-oidc")
            .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        var descriptor = new OpenIddictApplicationDescriptor
        {
            ClientId = DcmsOAuth.Clients.Edge,
            ClientSecret = secret,
            ClientType = ClientTypes.Confidential,
            ConsentType = ConsentTypes.Implicit,
            DisplayName = "DCMS Edge",
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
        foreach (var uri in redirects) descriptor.RedirectUris.Add(new Uri(uri));
        foreach (var uri in postLogout) descriptor.PostLogoutRedirectUris.Add(new Uri(uri));

        var existing = await manager.FindByClientIdAsync(DcmsOAuth.Clients.Edge, ct);
        if (existing is null)
        {
            await manager.CreateAsync(descriptor, ct);
            logger.LogInformation("Seeded edge OIDC client with {Count} redirect URI(s).", redirects.Length);
            return;
        }

        // Converges, for the reason spelled out on EnsurePublicSpaClientAsync: a client first
        // created on a host whose configuration was wrong stayed wrong for the life of that
        // database, and the only symptom was OpenIddict refusing the sign-in.
        var current = new OpenIddictApplicationDescriptor();
        await manager.PopulateAsync(current, existing, ct);

        var urisChanged = !current.RedirectUris.SetEquals(descriptor.RedirectUris)
                          || !current.PostLogoutRedirectUris.SetEquals(descriptor.PostLogoutRedirectUris);
        if (urisChanged)
        {
            current.RedirectUris.Clear();
            current.PostLogoutRedirectUris.Clear();
            foreach (var uri in descriptor.RedirectUris) current.RedirectUris.Add(uri);
            foreach (var uri in descriptor.PostLogoutRedirectUris) current.PostLogoutRedirectUris.Add(uri);
            await manager.PopulateAsync(existing, current, ct);
        }

        // The SECRET is written on every run, unconditionally, and it cannot be compared first:
        // OpenIddict stores a hash, so the value read back is never the value configured.
        //
        // This used to sit behind the URI check above and return early with it. Rotating
        // EDGE_OIDC_CLIENT_SECRET therefore updated .env, updated the edge, and left identity
        // holding the OLD secret -- and the whole symptom is OpenIddict answering
        // `invalid_client`, on a sign-in that worked yesterday, for a value nobody can read back
        // to compare. Now the configured value is simply what is stored, every time.
        await manager.UpdateAsync(existing, secret, ct);
        logger.LogInformation(
            urisChanged
                ? "Updated edge OIDC client redirect URIs and secret from configuration."
                : "Edge OIDC client secret converged from configuration.");
    }

    /// <summary>
    /// Deletes the Grafana OIDC client, which nothing uses now that the edge signs operators in.
    ///
    /// <para>Deleted rather than left in place. Its role mapping was the thing standing between
    /// any DCMS login and a platform-wide read of every tenant's usage, audit trail and traces —
    /// so a live client id, a live secret and a live redirect URI pointing at Grafana's
    /// <c>generic_oauth</c> callback is a credential that still works the moment somebody
    /// re-enables that block. An unused credential is not a dormant one.</para>
    /// </summary>
    private async Task RetireGrafanaClientAsync(IOpenIddictApplicationManager manager, CancellationToken ct)
    {
        if (await manager.FindByClientIdAsync(DcmsOAuth.Clients.Grafana, ct) is not { } grafana)
        {
            return;
        }

        await manager.DeleteAsync(grafana, ct);
        logger.LogInformation(
            "Deleted the retired Grafana OIDC client; Grafana is signed in by the edge now.");
    }

    /// <summary>
    /// A confidential client-credentials client, created or brought back in line.
    ///
    /// <para>Converges its scopes for the same reason the public SPA clients do: a service that
    /// gains or loses a scope must actually gain or lose it on a database that already exists,
    /// not only on a fresh one. The secret is set on create and then left alone — rotating it
    /// is an operator action with its own coordination, and quietly overwriting theirs from a
    /// config default would break every service holding the old one.</para>
    /// </summary>
    private async Task EnsureServiceClientAsync(
        IOpenIddictApplicationManager manager,
        string clientId,
        string displayName,
        string secret,
        IReadOnlyList<string> scopes,
        CancellationToken ct)
    {
        var descriptor = new OpenIddictApplicationDescriptor
        {
            ClientId = clientId,
            ClientSecret = secret,
            ClientType = ClientTypes.Confidential,
            DisplayName = displayName,
            Permissions =
            {
                Permissions.Endpoints.Token,
                Permissions.GrantTypes.ClientCredentials,
            },
        };
        foreach (var scope in scopes)
        {
            descriptor.Permissions.Add(Permissions.Prefixes.Scope + scope);
        }

        var existing = await manager.FindByClientIdAsync(clientId, ct);
        if (existing is null)
        {
            await manager.CreateAsync(descriptor, ct);
            logger.LogInformation("Seeded {Client} with scopes {Scopes}.", clientId, string.Join(", ", scopes));
            return;
        }

        var current = new OpenIddictApplicationDescriptor();
        await manager.PopulateAsync(current, existing, ct);

        var wanted = descriptor.Permissions
            .Where(p => p.StartsWith(Permissions.Prefixes.Scope, StringComparison.Ordinal))
            .ToHashSet(StringComparer.Ordinal);
        var held = current.Permissions
            .Where(p => p.StartsWith(Permissions.Prefixes.Scope, StringComparison.Ordinal))
            .ToHashSet(StringComparer.Ordinal);
        if (held.SetEquals(wanted))
        {
            return;
        }

        foreach (var scope in held) current.Permissions.Remove(scope);
        foreach (var scope in wanted) current.Permissions.Add(scope);
        await manager.PopulateAsync(existing, current, ct);
        await manager.UpdateAsync(existing, ct);

        logger.LogInformation(
            "Converged {Client} scopes. Was: {Before}. Now: {After}.",
            clientId, string.Join(", ", held), string.Join(", ", wanted));
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

        /*
         * Scope permissions converge too, and that is a fix rather than an addition.
         *
         * This method used to return here whenever the URIs matched, on the reasoning that
         * configuration owns the URIs and an operator owns everything else. The consequence was
         * that the SCOPES a client may request could never be changed by a deploy: editing this
         * file changed what a fresh database got and nothing at all on a database that already
         * existed. Removing a scope from a shipped client was a silent no-op, which is the
         * worst possible outcome for a change whose entire purpose is to take a permission away.
         *
         * Only the scope prefix is converged. Endpoint, grant-type and response-type
         * permissions are left exactly as an operator left them.
         */
        var wantedScopes = descriptor.Permissions
            .Where(p => p.StartsWith(Permissions.Prefixes.Scope, StringComparison.Ordinal))
            .ToHashSet(StringComparer.Ordinal);
        var currentScopes = current.Permissions
            .Where(p => p.StartsWith(Permissions.Prefixes.Scope, StringComparison.Ordinal))
            .ToHashSet(StringComparer.Ordinal);

        var urisMatch = current.RedirectUris.SetEquals(descriptor.RedirectUris)
            && current.PostLogoutRedirectUris.SetEquals(descriptor.PostLogoutRedirectUris);
        var scopesMatch = currentScopes.SetEquals(wantedScopes);
        if (urisMatch && scopesMatch)
        {
            return;
        }

        var before = string.Join(", ", current.RedirectUris.Select(u => u.ToString()));
        current.RedirectUris.Clear();
        current.PostLogoutRedirectUris.Clear();
        foreach (var uri in descriptor.RedirectUris) current.RedirectUris.Add(uri);
        foreach (var uri in descriptor.PostLogoutRedirectUris) current.PostLogoutRedirectUris.Add(uri);

        foreach (var scope in currentScopes) current.Permissions.Remove(scope);
        foreach (var scope in wantedScopes) current.Permissions.Add(scope);

        await manager.PopulateAsync(existing, current, ct);
        await manager.UpdateAsync(existing, ct);

        logger.LogInformation(
            "Converged {Client}. URIs was: {Before}. Now: {After}. Scopes now: {Scopes}.",
            clientId,
            string.IsNullOrEmpty(before) ? "(none)" : before,
            string.Join(", ", descriptor.RedirectUris.Select(u => u.ToString())),
            string.Join(", ", wantedScopes));
    }

}
