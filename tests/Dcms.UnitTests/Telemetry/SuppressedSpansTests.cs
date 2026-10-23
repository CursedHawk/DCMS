using System.Diagnostics;
using OpenTelemetry;
using OpenTelemetry.Trace;

namespace Dcms.UnitTests.Telemetry;

/// <summary>
/// TenantGucInterceptor relies on this to keep its per-connection <c>set_config</c> out of every trace:
/// the SDK samples nothing started inside a SuppressInstrumentationScope, across an await too.
/// </summary>
public class SuppressedSpansTests
{
    [Fact]
    public async Task Nothing_started_inside_the_scope_is_recorded()
    {
        using var source = new ActivitySource("dcms.test.suppressed");
        using var provider = Sdk.CreateTracerProviderBuilder().AddSource(source.Name).Build();

        using (SuppressInstrumentationScope.Begin())
        {
            await Task.Yield();
            source.StartActivity("set_config").Should().BeNull();
        }
        using var after = source.StartActivity("query");
        after.Should().NotBeNull("the scope ends with its block");
    }
}
