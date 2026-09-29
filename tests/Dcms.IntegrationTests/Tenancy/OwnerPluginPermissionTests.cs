extern alias AdminApiApp;

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AdminApiApp::Dcms.AdminApi.Tenancy;
using Dcms.PluginSdk.Abstractions;
using Dcms.Shared.Audit;
using Dcms.Shared.Data.Rls;
using Dcms.Shared.Data.Tenancy;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Dcms.IntegrationTests.Tenancy;

/// <summary>
/// Owner means everything, plugin permissions included: a new tenant's Owner role is seeded with
/// every plugin's keys, and an existing tenant created before that (or before a plugin gained a
/// permission) is brought up to date by the startup backfill.
/// </summary>
[Collection(AdminApiCollection.Name)]
public sealed class OwnerPluginPermissionTests(AdminApiFixture fixture)
{
    [DockerFact]
    public async Task Owner_holds_every_plugin_permission_and_the_backfill_restores_missing_ones()
    {
        var ct = TestContext.Current.CancellationToken;
        var owner = Guid.NewGuid();
        var slug = "own-" + Guid.NewGuid().ToString("N")[..8];
        var req = new HttpRequestMessage(HttpMethod.Post, "/api/admin/tenants")
        {
            Headers = { { "X-Test-Sub", Guid.NewGuid().ToString() }, { "X-Test-Roles", "SuperAdmin" } },
            Content = JsonContent.Create(new { slug, name = slug, ownerUserId = owner, ownerEmail = $"{owner:N}@dcms.test" }),
        };
        var res = await fixture.Factory.CreateClient().SendAsync(req, ct);
        res.StatusCode.Should().Be(HttpStatusCode.Created);
        var tenantId = (await res.Content.ReadFromJsonAsync<JsonElement>(ct)).GetProperty("tenantId").GetGuid();

        using var scope = fixture.Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TenancyDbContext>();
        var catalog = scope.ServiceProvider.GetRequiredService<IPluginCatalog>();
        var expected = OwnerPermissionBackfill.OwnerPermissions(catalog);
        expected.Should().Contain(["plugin:visitor-auth:read", "plugin:forms:read"]);

        async Task<List<string>> Held()
        {
            using var rls = RlsScope.Tenant(tenantId);
            var ownerRole = await db.TenantRoles.IgnoreQueryFilters().AsNoTracking()
                .Where(r => r.TenantId == tenantId && r.Name == TenantProvisioning.OwnerRole)
                .Select(r => r.Id).SingleAsync(ct);
            return await db.TenantRolePermissions.IgnoreQueryFilters().AsNoTracking()
                .Where(p => p.TenantRoleId == ownerRole)
                .Select(p => p.Permission).ToListAsync(ct);
        }

        (await Held()).Should().Contain(expected, "a new Owner role is seeded with platform and plugin permissions");

        // Simulate a tenant created before plugin permissions reached Owners.
        using (RlsScope.Tenant(tenantId))
        {
            await db.TenantRolePermissions.IgnoreQueryFilters()
                .Where(p => p.TenantId == tenantId && p.Permission.StartsWith("plugin:"))
                .ExecuteDeleteAsync(ct);
        }
        (await Held()).Should().NotContain("plugin:visitor-auth:read");

        await OwnerPermissionBackfill.ApplyAsync(
            db, catalog, scope.ServiceProvider.GetRequiredService<IAuditRecorder>(), NullLogger.Instance, ct);

        (await Held()).Should().Contain(expected);
    }
}
