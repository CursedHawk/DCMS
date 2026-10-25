using Dcms.Shared.Data.Tenancy;
using Dcms.Shared.Kernel.Abstractions;
using Finbuckle.MultiTenant.Extensions;
using Finbuckle.MultiTenant.AspNetCore.Extensions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace Dcms.UnitTests.Data;

/// <summary>
/// AuditMiddleware wraps UseMultiTenant and records refusals after the pipeline returns. The
/// accessor's AsyncLocal does not flow back out, so every such record landed at platform scope.
/// </summary>
public sealed class FinbuckleTenantContextTests
{
    [Fact]
    public async Task The_tenant_is_still_known_after_the_middleware_that_resolved_it_returns()
    {
        var services = new ServiceCollection();
        services.AddMultiTenant<Tenant>();
        services.AddHttpContextAccessor();
        services.AddScoped<ITenantContext, FinbuckleTenantContext>();
        await using var provider = services.BuildServiceProvider();
        await using var scope = provider.CreateAsyncScope();

        var http = new DefaultHttpContext { RequestServices = scope.ServiceProvider };
        scope.ServiceProvider.GetRequiredService<IHttpContextAccessor>().HttpContext = http;
        var tenantId = Guid.NewGuid();

        // What UseMultiTenant does, one await down from the caller.
        await ResolveInsideThePipelineAsync(http, new Tenant { Id = tenantId.ToString(), Identifier = "acme" });

        var tenant = scope.ServiceProvider.GetRequiredService<ITenantContext>();
        tenant.TenantId.Should().Be(tenantId);
        tenant.TenantSlug.Should().Be("acme");
    }

    private static async Task ResolveInsideThePipelineAsync(HttpContext http, Tenant tenant)
    {
        await Task.Yield();
        http.SetTenantInfo(tenant, resetServiceProviderScope: false);
    }
}
