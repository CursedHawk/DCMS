using Dcms.Shared.Data.Rls;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Dcms.UnitTests.Data;

/// <summary>
/// ADR 0015. <see cref="RlsScope"/> decides whose rows the database will return, so the ways it
/// could stay wide by accident are the ones worth pinning: an inner block switching off an outer
/// one, a double dispose, a scope leaking onto another flow — and a tenant block inside a
/// platform one failing to narrow, which would quietly turn every scan's per-item work into
/// all-tenant work.
/// </summary>
public class RlsScopeTests
{
    private static readonly Guid A = Guid.NewGuid();
    private static readonly Guid B = Guid.NewGuid();

    [Fact]
    public void Nested_blocks_restore_the_enclosing_state()
    {
        RlsScope.IsPlatform.Should().BeFalse();
        using (RlsScope.Platform())
        {
            using (RlsScope.Platform())
            {
                RlsScope.IsPlatform.Should().BeTrue();
            }
            RlsScope.IsPlatform.Should().BeTrue("the outer block is still open");
        }
        RlsScope.IsPlatform.Should().BeFalse();
        RlsScope.TenantOverride.Should().BeNull();
    }

    [Fact]
    public void A_tenant_block_inside_a_platform_block_narrows_and_leaving_it_widens_again()
    {
        using (RlsScope.Platform())
        {
            using (RlsScope.Tenant(A))
            {
                RlsScope.IsPlatform.Should().BeFalse("per-item work inside a scan is one tenant's work");
                RlsScope.TenantOverride.Should().Be(A);

                using (RlsScope.Tenant(B))
                {
                    RlsScope.TenantOverride.Should().Be(B);
                }
                RlsScope.TenantOverride.Should().Be(A);
            }
            RlsScope.IsPlatform.Should().BeTrue();
            RlsScope.TenantOverride.Should().BeNull();
        }
    }

    [Fact]
    public void Disposing_twice_does_not_reopen_or_close_anything_else()
    {
        var inner = RlsScope.Platform();
        inner.Dispose();
        using (RlsScope.Tenant(A))
        {
            inner.Dispose();
            RlsScope.TenantOverride.Should().Be(A);
            RlsScope.IsPlatform.Should().BeFalse();
        }
        RlsScope.TenantOverride.Should().BeNull();
    }

    [Fact]
    public async Task It_flows_into_work_started_inside_it_and_not_into_anything_else()
    {
        using (RlsScope.Platform())
        {
            (await Task.Run(() => RlsScope.IsPlatform)).Should().BeTrue();
        }
        (await Task.Run(() => RlsScope.IsPlatform)).Should().BeFalse();
    }

    [Theory]
    [InlineData(null, 0)]
    [InlineData("false", 0)]
    [InlineData("true", 1)]
    public void The_interceptor_is_in_the_container_only_when_enforcement_is_on(string? flag, int expected)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(flag is null ? [] : [new("Rls:Enforce", flag)])
            .Build();

        var services = new ServiceCollection().AddDcmsRlsEnforcement(configuration);

        services.Count(d => d.ServiceType == typeof(IInterceptor)).Should().Be(expected);
    }
}
