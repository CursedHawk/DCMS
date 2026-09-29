using System.Collections.Concurrent;
using System.Text.Json;
using Dcms.PluginSdk.Abstractions;
using Dcms.PluginSdk.Abstractions.Contracts;
using Dcms.PluginSdk.Abstractions.Platform;
using Dcms.PluginSdk.Runtime.Platform;
using Dcms.Shared.Caching;
using Dcms.Shared.Contracts.Events;
using Dcms.Shared.Messaging;
using Dcms.Shared.Messaging.Email;
using Dcms.Shared.Storage;
using Microsoft.Extensions.Options;

namespace Dcms.PluginSdk.Tests.Platform;

/// <summary>
/// The platform contracts over services with no RLS of their own (object storage, Redis, the
/// email queue, the notification bus). Their isolation is a prefix or a field stamped from the
/// caller's context, so these tests check that the stamp is always applied and that no input can
/// escape it.
/// </summary>
public class PlatformContractTests
{
    private static readonly Guid Tenant = Guid.Parse("11111111-1111-1111-1111-111111111111");

    private sealed record Ctx(string PluginId) : IPluginContext
    {
        public Guid TenantId => Tenant;
        public PluginInstanceContext? Instance => null;
        public PluginActor Actor => PluginActor.System;
        public IPluginContracts Contracts => throw new NotSupportedException();
public IPluginHooks Hooks => throw new NotSupportedException();
    }

    [Theory]
    [InlineData("../other-plugin/x")]
    [InlineData("a/../../x")]
    [InlineData("/abs")]
    [InlineData("a\\b")]
    [InlineData("./x")]
    [InlineData("")]
    public void Blob_keys_cannot_leave_the_plugin_area(string key)
    {
        var blobs = new PluginBlobs(new Ctx("forms"), new MemoryStorage(), Options.Create(new StorageOptions()));

        var act = () => blobs.Resolve(key);

        act.Should().Throw<ContractValidationException>();
    }

    [Fact]
    public async Task Blobs_live_under_the_tenant_and_plugin_prefix()
    {
        var storage = new MemoryStorage();
        var blobs = new PluginBlobs(new Ctx("forms"), storage, Options.Create(new StorageOptions()));
        var ct = TestContext.Current.CancellationToken;

        await blobs.PutAsync(new BlobPut("exports/a.csv", "text/csv", "x,y"u8.ToArray()), ct);

        storage.Objects.Keys.Should().Equal($"dcms-media/tenants/{Tenant}/plugins/forms/exports/a.csv");
        (await blobs.ListAsync(new BlobPrefix(), ct)).Keys.Should().Equal("exports/a.csv");
        (await blobs.GetAsync(new BlobKey("exports/a.csv"), ct))!.Content.Should().Equal("x,y"u8.ToArray());

        var other = new PluginBlobs(new Ctx("events"), storage, Options.Create(new StorageOptions()));
        (await other.GetAsync(new BlobKey("exports/a.csv"), ct)).Should().BeNull();
        (await other.ListAsync(new BlobPrefix(), ct)).Keys.Should().BeEmpty();
    }

    [Fact]
    public void Cache_keys_are_prefixed_by_tenant_and_plugin()
    {
        new PluginCache(new Ctx("forms"), new MemoryCache()).Resolve("k")
            .Should().Be($"t:{Tenant}:p:forms:k");
    }

    [Fact]
    public async Task Email_is_stamped_and_rate_limited()
    {
        var queue = new RecordingQueue();
        var email = new PluginEmail(new Ctx("forms"), queue, new MemoryCache());
        var ct = TestContext.Current.CancellationToken;

        await email.SendAsync(new EmailSend(["a@x.test"], "Hi", "<p>hi</p>", DedupeKey: "sub-1"), ct);

        var sent = queue.Sent.Single();
        sent.TenantId.Should().Be(Tenant);
        sent.Purpose.Should().Be("plugin:forms");
        sent.DedupeKey.Should().Be("plugin:forms:sub-1");

        var many = Enumerable.Range(0, 50).Select(i => $"r{i}@x.test").ToList();
        for (var i = 0; i < 19; i++)
        {
            await email.SendAsync(new EmailSend(many, "Bulk", "<p/>"), ct);
        }
        var over = () => email.SendAsync(new EmailSend(many, "Bulk", "<p/>"), ct);
        await over.Should().ThrowAsync<ContractLimitException>();
    }

    [Theory]
    [InlineData("not-an-address")]
    [InlineData("a b@x.test")]
    public async Task Email_refuses_bad_recipients(string address)
    {
        var email = new PluginEmail(new Ctx("forms"), new RecordingQueue(), new MemoryCache());

        var act = () => email.SendAsync(new EmailSend([address], "Hi", "<p/>"), TestContext.Current.CancellationToken);

        await act.Should().ThrowAsync<ContractValidationException>();
    }

    [Fact]
    public async Task Email_subject_cannot_inject_headers()
    {
        var email = new PluginEmail(new Ctx("forms"), new RecordingQueue(), new MemoryCache());

        var act = () => email.SendAsync(new EmailSend(["a@x.test"], "Hi\r\nBcc: evil@x.test", "<p/>"), TestContext.Current.CancellationToken);

        await act.Should().ThrowAsync<ContractValidationException>();
    }

    [Fact]
    public async Task Notifications_are_stamped_with_tenant_and_namespaced_dedupe()
    {
        var bus = new RecordingBus();
        var notifications = new PluginNotifications(new Ctx("forms"), bus);

        await notifications.RaiseAsync(
            new NotificationRaise("New lead", "Someone asked about pricing.", "content:read", "lead-42", LinkPath: "/forms"),
            TestContext.Current.CancellationToken);

        var (subject, evt) = bus.Published.Single();
        subject.Should().Be("notify.raise");
        var raised = (NotificationRaiseRequested)evt;
        raised.TenantId.Should().Be(Tenant);
        raised.DedupeKey.Should().Be("plugin:forms:lead-42");
        raised.Kind.Should().Be("plugin.message");
        raised.TitleKey.Should().Be("notifications.kinds.plugin_message.title");
        JsonDocument.Parse(raised.ParamsJson).RootElement.GetProperty("title").GetString().Should().Be("New lead");
    }

    [Fact]
    public async Task Notification_links_stay_inside_the_admin()
    {
        var notifications = new PluginNotifications(new Ctx("forms"), new RecordingBus());

        var act = () => notifications.RaiseAsync(
            new NotificationRaise("t", "b", "content:read", "k", LinkPath: "//evil.test"), TestContext.Current.CancellationToken);

        await act.Should().ThrowAsync<ContractValidationException>();
    }

    // --- fakes -------------------------------------------------------------------------------

    private sealed class MemoryStorage : IObjectStorage
    {
        public ConcurrentDictionary<string, (byte[] Bytes, string ContentType)> Objects { get; } = new();

        public async Task PutAsync(string bucket, string key, Stream content, long size, string contentType, CancellationToken ct = default)
        {
            using var buffer = new MemoryStream();
            await content.CopyToAsync(buffer, ct);
            Objects[$"{bucket}/{key}"] = (buffer.ToArray(), contentType);
        }

        public Task<Stream> GetAsync(string bucket, string key, CancellationToken ct = default) =>
            Task.FromResult<Stream>(new MemoryStream(Objects[$"{bucket}/{key}"].Bytes));

        public async Task GetToAsync(string bucket, string key, Stream destination, long? offset = null, long? length = null, CancellationToken ct = default) =>
            await destination.WriteAsync(Objects[$"{bucket}/{key}"].Bytes, ct);

        public Task<StoredObjectInfo?> StatAsync(string bucket, string key, CancellationToken ct = default) =>
            Task.FromResult(Objects.TryGetValue($"{bucket}/{key}", out var o) ? new StoredObjectInfo(o.Bytes.Length, o.ContentType) : null);

        public Task<bool> ExistsAsync(string bucket, string key, CancellationToken ct = default) =>
            Task.FromResult(Objects.ContainsKey($"{bucket}/{key}"));

        public Task DeleteAsync(string bucket, string key, CancellationToken ct = default)
        {
            Objects.TryRemove($"{bucket}/{key}", out _);
            return Task.CompletedTask;
        }

        public async IAsyncEnumerable<string> ListKeysAsync(string bucket, string prefix, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
        {
            foreach (var k in Objects.Keys.Where(k => k.StartsWith($"{bucket}/{prefix}", StringComparison.Ordinal)).Order())
            {
                yield return k[(bucket.Length + 1)..];
            }
            await Task.CompletedTask;
        }

        public Task<int> DeletePrefixAsync(string bucket, string prefix, CancellationToken ct = default) => throw new NotSupportedException();
    }

    private sealed class MemoryCache : ICacheService
    {
        private readonly ConcurrentDictionary<string, object?> _values = new();

        public Task<T?> GetAsync<T>(string key, CancellationToken ct = default) =>
            Task.FromResult(_values.TryGetValue(key, out var v) ? (T?)v : default);

        public Task SetAsync<T>(string key, T value, TimeSpan? ttl = null, CancellationToken ct = default)
        {
            _values[key] = value;
            return Task.CompletedTask;
        }

        public Task RemoveAsync(string key, CancellationToken ct = default)
        {
            _values.TryRemove(key, out _);
            return Task.CompletedTask;
        }

        public Task<long> IncrementAsync(string key, CancellationToken ct = default) => IncrementAsync(key, 1, TimeSpan.FromHours(1), ct);

        public Task<long> IncrementAsync(string key, long by, TimeSpan ttl, CancellationToken ct = default) =>
            Task.FromResult((long)_values.AddOrUpdate(key, by, (_, old) => (long)old! + by)!);
    }

    private sealed class RecordingQueue : IEmailQueue
    {
        public List<EmailMessage> Sent { get; } = [];

        public Task EnqueueAsync(EmailMessage message, CancellationToken cancellationToken = default)
        {
            Sent.Add(message);
            return Task.CompletedTask;
        }
    }

    private sealed class RecordingBus : IEventPublisher
    {
        public List<(string Subject, object Event)> Published { get; } = [];

        public ValueTask PublishAsync<T>(string subject, T @event, CancellationToken ct = default, string? messageId = null)
            where T : Dcms.Shared.Contracts.Events.IDcmsEvent
        {
            Published.Add((subject, @event!));
            return ValueTask.CompletedTask;
        }
    }
}
