using System.Text.Json;
using Dcms.PluginSdk.Abstractions;
using Dcms.PluginSdk.Abstractions.Contracts;
using Dcms.PluginSdk.Abstractions.Platform;
using Dcms.PluginSdk.Runtime;
using Dcms.PluginSdk.Runtime.Contracts;
using Dcms.PluginSdk.Runtime.Hosting;
using Dcms.PluginSdk.Runtime.Platform;
using Dcms.PluginSdk.Tests.Contracts;
using Dcms.Shared.Contracts.Events;
using Dcms.Shared.Messaging;
using Microsoft.Extensions.DependencyInjection;

namespace Dcms.PluginSdk.Tests.Platform;

public class EventsAndJobsTests
{
    private static readonly Guid Tenant = Guid.NewGuid();

    private sealed class NoopJob : IPluginJobHandler
    {
        public Task RunAsync(JsonElement? payload, IPluginContext context, CancellationToken ct) => Task.CompletedTask;
    }

    private static readonly PluginRegistry Registry = new(
    [
        new TestPlugin("greeter", provides: [ContractProvision.Of<IGreeter, Greeter>()]),
        new TestPlugin("forms"),
        new JobsPlugin(),
    ]);

    private sealed class JobsPlugin : IPlugin
    {
        public PluginManifest Manifest { get; } = PluginManifest.Create(
            "sync", "Sync", "Syncs.", allowMultipleInstances: false,
            jobs: [JobDeclaration.Of<NoopJob>("refresh", TimeSpan.FromHours(1))]);

        public void ConfigureServices(IServiceCollection services)
        {
        }

        public void MapEndpoints(IPluginEndpointBuilder endpoints)
        {
        }

        public OpenApiFragment BuildOpenApiFragment(PluginInstanceContext instance) => OpenApiFragment.Empty;
    }

    private sealed record Ctx(string PluginId, PluginInstanceContext? Instance = null) : IPluginContext
    {
        public Guid TenantId => Tenant;
        public PluginActor Actor => PluginActor.System;
        public IPluginContracts Contracts => throw new NotSupportedException();
public IPluginHooks Hooks => throw new NotSupportedException();
    }

    private sealed class Bus : IEventPublisher
    {
        public List<(string Subject, IDcmsEvent Event, string? MessageId)> Published { get; } = [];

        public ValueTask PublishAsync<T>(string subject, T @event, CancellationToken ct = default, string? messageId = null)
            where T : IDcmsEvent
        {
            Published.Add((subject, @event, messageId));
            return ValueTask.CompletedTask;
        }
    }

    [Fact]
    public async Task Provider_publishes_its_declared_event()
    {
        var bus = new Bus();
        var events = new PluginEvents(new Ctx("greeter"), Registry, bus);

        await events.PublishAsync(
            new EventPublish("test.greeted", JsonSerializer.SerializeToElement(new Greeted("Ada"), JsonSerializerOptions.Web)),
            TestContext.Current.CancellationToken);

        var (subject, evt, _) = bus.Published.Single();
        subject.Should().Be("plugins.events.greeter.test.greeted");
        var published = (PluginEventPublished)evt;
        published.TenantId.Should().Be(Tenant);
        published.PublisherPluginId.Should().Be("greeter");
    }

    [Fact]
    public async Task A_plugin_cannot_forge_another_plugins_event()
    {
        var events = new PluginEvents(new Ctx("forms"), Registry, new Bus());

        var act = () => events.PublishAsync(
            new EventPublish("test.greeted", JsonSerializer.SerializeToElement(new { name = "x" })),
            TestContext.Current.CancellationToken);

        await act.Should().ThrowAsync<ContractValidationException>().WithMessage("*forms*does not provide*test.greeted*");
    }

    [Fact]
    public async Task Event_payload_must_match_the_contract()
    {
        var events = new PluginEvents(new Ctx("greeter"), Registry, new Bus());

        var act = () => events.PublishAsync(
            new EventPublish("test.greeted", JsonSerializer.SerializeToElement(new { name = 42 })),
            TestContext.Current.CancellationToken);

        await act.Should().ThrowAsync<ContractValidationException>().WithMessage("*does not match*");
    }

    [Fact]
    public async Task Jobs_are_enqueued_only_by_their_plugin_with_delay_and_dedupe()
    {
        var bus = new Bus();
        var jobs = new PluginJobs(new Ctx("sync"), Registry, bus);
        var ct = TestContext.Current.CancellationToken;

        await jobs.EnqueueAsync(new JobEnqueue("refresh", DelaySeconds: 60, DedupeKey: "account-7"), ct);

        var (subject, evt, messageId) = bus.Published.Single();
        subject.Should().Be("plugins.jobs.sync.refresh");
        ((PluginJobRequested)evt).NotBefore.Should().BeCloseTo(DateTimeOffset.UtcNow.AddSeconds(60), TimeSpan.FromSeconds(5));
        messageId.Should().Be($"{Tenant}:sync:refresh:account-7");

        var other = new PluginJobs(new Ctx("forms"), Registry, bus);
        var act = () => other.EnqueueAsync(new JobEnqueue("refresh"), ct);
        await act.Should().ThrowAsync<ContractValidationException>();
    }

    [Fact]
    public void Event_names_become_subject_tokens_so_they_must_be_kebab()
    {
        var act = () => ContractDescriptorBuilder.Build(typeof(IBadEventContract));

        act.Should().Throw<InvalidOperationException>().WithMessage("*event name 'bad event'*");
    }

    [ContractEvent("bad event")]
    public sealed record BadEvent : IPluginEvent;

    [DcmsContract("test.bad-events", 1, Events = [typeof(BadEvent)])]
    public interface IBadEventContract
    {
        [Operation(OpRisk.Read)]
        Task<GreetResult> NothingAsync(CancellationToken ct);
    }

    [Fact]
    public async Task Handler_runner_skips_a_tenant_without_the_plugin_and_serves_one_with_it()
    {
        var instance = Instances.Of(Tenant, "greeter", "hi");
        var services = new ServiceCollection();
        services.AddSingleton(Registry);
        services.AddSingleton<IPluginInstanceStore>(new FakeInstanceStore(instance));
        services.AddScoped<PluginContextFactory>();
        services.AddScoped<PluginContextAccessor>();
        await using var sp = services.BuildServiceProvider();
        var runner = new PluginHandlerRunner(sp);
        var ct = TestContext.Current.CancellationToken;

        IPluginContext? seen = null;
        (await runner.RunAsync(Tenant, "forms", null, (c, _) => { seen = c; return Task.CompletedTask; }, ct))
            .Should().BeFalse("forms has no enabled instance in this tenant");
        seen.Should().BeNull();

        (await runner.RunAsync(Tenant, "greeter", Guid.NewGuid(), (c, _) => { seen = c; return Task.CompletedTask; }, ct))
            .Should().BeFalse("the job's instance is no longer enabled");

        (await runner.RunAsync(Tenant, "greeter", instance.InstanceId, (c, s) =>
        {
            seen = c;
            s.GetRequiredService<PluginContextAccessor>().Current.Should().BeSameAs(c);
            return Task.CompletedTask;
        }, ct)).Should().BeTrue();
        seen!.TenantId.Should().Be(Tenant);
        seen.Instance!.Slug.Should().Be("hi");
    }
}
