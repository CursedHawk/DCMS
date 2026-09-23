using Dcms.Shared.Data.Rls;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Dcms.UnitTests.Data;

/// <summary>
/// ADR 0015. <see cref="PlatformScope"/> is the one switch that widens a query past its tenant,
/// so the ways it could stay on by accident are the ones worth pinning: an inner block switching
/// off an outer one, a double dispose, and a scope entered on one flow leaking onto another.
/// </summary>
public class PlatformScopeTests
{
    [Fact]
    public void Nested_blocks_restore_rather_than_switch_off()
    {
        PlatformScope.IsActive.Should().BeFalse();
        using (PlatformScope.Enter())
        {
            using (PlatformScope.Enter())
            {
                PlatformScope.IsActive.Should().BeTrue();
            }
            PlatformScope.IsActive.Should().BeTrue("the outer block is still open");
        }
        PlatformScope.IsActive.Should().BeFalse();
    }

    [Fact]
    public void Disposing_twice_does_not_reopen_or_close_anything_else()
    {
        var inner = PlatformScope.Enter();
        inner.Dispose();
        using (PlatformScope.Enter())
        {
            inner.Dispose();
            PlatformScope.IsActive.Should().BeTrue();
        }
        PlatformScope.IsActive.Should().BeFalse();
    }

    [Fact]
    public async Task It_flows_into_work_started_inside_it_and_not_into_anything_else()
    {
        using (PlatformScope.Enter())
        {
            (await Task.Run(() => PlatformScope.IsActive)).Should().BeTrue();
        }
        (await Task.Run(() => PlatformScope.IsActive)).Should().BeFalse();
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
