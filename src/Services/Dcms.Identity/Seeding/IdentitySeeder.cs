using Dcms.Identity.Data;
using Dcms.Identity.Domain;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using OpenIddict.Abstractions;
using static OpenIddict.Abstractions.OpenIddictConstants;

namespace Dcms.Identity.Seeding;

/// <summary>
/// Idempotent startup seeding: applies migrations, then creates global roles,
/// the SuperAdmin user, OpenIddict scopes and the SPA + service clients.
/// Controlled by Identity:Seed (default true in dev).
/// </summary>
public sealed class IdentitySeeder(
    IServiceProvider services,
    IConfiguration configuration,
    ILogger<IdentitySeeder> logger) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        using var scope = services.CreateScope();
        var sp = scope.ServiceProvider;

        if (configuration.GetValue("Identity:Migrate", true))
        {
            var db = sp.GetRequiredService<IdentityDbContext>();
            await db.Database.MigrateAsync(cancellationToken);
            logger.LogInformation("Identity database migrated.");
        }

        if (!configuration.GetValue("Identity:Seed", true))
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
        logger.LogInformation("Seeded SuperAdmin {Email}.", email);
    }

    private static async Task SeedScopesAsync(IServiceProvider sp, CancellationToken ct)
    {
        var manager = sp.GetRequiredService<IOpenIddictScopeManager>();

        await EnsureScopeAsync(manager, DcmsOAuth.Scopes.Admin, "DCMS Admin API", DcmsOAuth.Resources.AdminApi, ct);
        await EnsureScopeAsync(manager, DcmsOAuth.Scopes.Ai, "DCMS AI Gateway", DcmsOAuth.Resources.AiGateway, ct);
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

        if (await manager.FindByClientIdAsync(DcmsOAuth.Clients.AdminSpa, ct) is null)
        {
            var descriptor = new OpenIddictApplicationDescriptor
            {
                ClientId = DcmsOAuth.Clients.AdminSpa,
                ClientType = ClientTypes.Public,
                ConsentType = ConsentTypes.Implicit,
                DisplayName = "DCMS Admin SPA",
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
                    Permissions.Prefixes.Scope + DcmsOAuth.Scopes.Admin,
                },
                Requirements = { Requirements.Features.ProofKeyForCodeExchange },
            };
            foreach (var uri in spaRedirects)
            {
                descriptor.RedirectUris.Add(new Uri(uri));
            }
            foreach (var uri in spaPostLogout)
            {
                descriptor.PostLogoutRedirectUris.Add(new Uri(uri));
            }
            await manager.CreateAsync(descriptor, ct);
            logger.LogInformation("Seeded admin SPA client.");
        }

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
                },
            }, ct);
            logger.LogInformation("Seeded admin-api service client.");
        }
    }
}
