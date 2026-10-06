using Dcms.IntegrationTests.Tenancy;
using Dcms.PluginSdk.Runtime.Hosting;
using Dcms.Shared.Contracts.Messaging;
using Microsoft.Extensions.DependencyInjection;
using NATS.Client.JetStream;
using NATS.Client.JetStream.Models;

namespace Dcms.IntegrationTests.Plugins;

/// <summary>
/// A plugin subscription's durable is created on first start and rebound on every start after.
/// The rebind used to ask for a different deliver policy than the one the durable was created
/// with, which JetStream refuses — live-chat's subscription retried every five seconds and never
/// consumed again after the first restart.
/// </summary>
[Collection(AdminApiCollection.Name)]
public sealed class PluginEventConsumerRestartTests(AdminApiFixture fixture)
{
    [DockerFact]
    public async Task A_subscription_binds_again_after_a_restart()
    {
        var ct = TestContext.Current.CancellationToken;
        var js = fixture.Factory.Services.GetRequiredService<INatsJSContext>();
        var durable = "test-restart-" + Guid.NewGuid().ToString("N")[..8];
        var subject = Subjects.PluginEventAnyPublisher("restart.check");

        await PluginEventConsumer.BindAsync(js, durable, subject, ct);
        var rebound = await PluginEventConsumer.BindAsync(js, durable, subject, ct);

        // Still "new": history is not replayed into a handler just because the host restarted.
        rebound.Info.Config.DeliverPolicy.Should().Be(ConsumerConfigDeliverPolicy.New);
    }
}
