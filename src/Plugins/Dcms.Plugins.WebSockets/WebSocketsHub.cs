using System.Collections.Concurrent;
using System.Text.Json;
using Dcms.Plugins.UserAuth.Api;
using Dcms.Plugins.VisitorAuth.Api;
using Dcms.Plugins.WebSockets.Api;
using Dcms.PluginSdk.Abstractions;
using Dcms.PluginSdk.Abstractions.Contracts;
using Dcms.PluginSdk.Abstractions.Platform;
using Dcms.PluginSdk.Runtime.Contracts;
using Dcms.Shared.Data.Rls;
using Dcms.Shared.Kernel.Abstractions;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Dcms.Plugins.WebSockets;

/// <summary>
/// The site hub (ADR 0037): <c>/hub/ws?instance={slug}</c> on a tenant site's own domain.
///
/// <para>Hub endpoints skip endpoint filters, so <see cref="OnConnectedAsync"/> does what the
/// plugin instance filter does for routes: the tenant is the one <c>SiteHostResolution</c> resolved
/// from the Host (never a query string), the request must come from a tenant site
/// (<c>X-Dcms-Site</c>; the edge's admin-host <c>/hub/**</c> route also reaches content-api), and
/// the instance must be enabled. Who the caller is and which channel permissions they hold are read
/// once, at connect, inside the upgrade request where the identity contracts see its headers.</para>
///
/// <para>Each call re-reads the instance's channels, so an edit applies to open connections and
/// disabling the instance stops them at their next call.</para>
/// </summary>
public sealed class WebSocketsHub(
    IServiceScopeFactory scopes,
    IOptions<WebSocketsOptions> options,
    ILogger<WebSocketsHub> logger) : Hub
{
    /// <summary>The method a browser receives a message on: (channel, key, payload, sender).</summary>
    public const string MessageMethod = "message";

    private const string StateKey = "dcms.ws";
    private const string SiteHeader = "X-Dcms-Site";

    public const int MaxSubscriptions = 50;
    public const int PublishesPerWindow = 20;
    public static readonly TimeSpan PublishWindow = TimeSpan.FromSeconds(10);

    // ponytail: per replica, so the real ceiling is cap × replicas; a Redis counter when
    // content-api runs replicas (nothing deploys them yet).
    private static readonly ConcurrentDictionary<Guid, int> ConnectionsPerTenant = new();

    private ConnectionState State => (ConnectionState)Context.Items[StateKey]!;

    public override async Task OnConnectedAsync()
    {
        var http = Context.GetHttpContext();
        var slug = http?.Request.Query["instance"].ToString();
        if (http is null || string.IsNullOrEmpty(slug) || !http.Request.Headers.ContainsKey(SiteHeader)
            || http.RequestServices.GetRequiredService<ITenantContext>().TenantId is not { } tenantId)
        {
            Context.Abort();
            return;
        }
        var ct = Context.ConnectionAborted;
        var factory = http.RequestServices.GetRequiredService<PluginContextFactory>();
        var instance = (await factory.EnabledInstancesAsync(tenantId, ct))
            .FirstOrDefault(i => i.PluginId == WebSocketsPlugin.PluginId && i.Slug == slug);
        if (instance is null)
        {
            Context.Abort();
            return;
        }

        var cap = options.Value.MaxConnectionsPerTenant;
        if (ConnectionsPerTenant.AddOrUpdate(tenantId, 1, (_, n) => n + 1) > cap)
        {
            ConnectionsPerTenant.AddOrUpdate(tenantId, 0, (_, n) => n - 1);
            logger.LogWarning("WebSockets connection refused: tenant {TenantId} is at its cap of {Cap} on this replica.", tenantId, cap);
            Context.Abort();
            return;
        }

        var state = new ConnectionState(tenantId, instance.EnvironmentId, instance.InstanceId, instance.Slug);
        Context.Items[StateKey] = state;
        try
        {
            var plugin = await factory.CreateAsync(tenantId, WebSocketsPlugin.PluginId, instance, factory.CurrentActor(), ct);
            state.UserId = (await plugin.Contracts.CurrentVisitorAsync(ct))?.Id;
            if (state.UserId is not null && plugin.Contracts.TryGet<IUserAccess>() is { } access)
            {
                var prefix = ChannelPermissions.Prefix(instance.Slug);
                state.Permissions = (await access.ListPermissionsAsync(ct)).Permissions
                    .Where(p => p.StartsWith(prefix, StringComparison.Ordinal))
                    .ToHashSet(StringComparer.Ordinal);
            }
        }
        catch (Exception ex)
        {
            // Identity is best effort: an identity provider that fails leaves the caller anonymous,
            // which only narrows what they may do.
            logger.LogWarning(ex, "Resolving the WebSockets caller failed; continuing anonymous.");
        }

        if (state.UserId is { } userId)
        {
            await Groups.AddToGroupAsync(Context.ConnectionId, ChannelGroups.User(state.TenantId, state.EnvironmentId, state.Slug, userId), ct);
        }
        await base.OnConnectedAsync();
    }

    public override Task OnDisconnectedAsync(Exception? exception)
    {
        if (Context.Items.ContainsKey(StateKey))
        {
            ConnectionsPerTenant.AddOrUpdate(State.TenantId, 0, (_, n) => Math.Max(0, n - 1));
        }
        return base.OnDisconnectedAsync(exception);
    }

    /// <summary>Start receiving a channel's messages (of one key, for a keyed channel).</summary>
    public async Task Subscribe(string channel, string? key = null)
    {
        var state = State;
        var definition = await ChannelAsync(channel);
        Refuse(Channels.CheckKey(definition, key));
        Refuse(Access.CheckSubscribe(definition, state, key));
        var group = ChannelGroups.Channel(state.TenantId, state.EnvironmentId, state.Slug, channel, key);
        lock (state)
        {
            if (!state.Subscriptions.Contains(group) && state.Subscriptions.Count >= MaxSubscriptions)
            {
                throw new HubException($"A connection may hold at most {MaxSubscriptions} subscriptions.");
            }
            state.Subscriptions.Add(group);
        }
        await Groups.AddToGroupAsync(Context.ConnectionId, group);
    }

    public async Task Unsubscribe(string channel, string? key = null)
    {
        var state = State;
        var group = ChannelGroups.Channel(state.TenantId, state.EnvironmentId, state.Slug, channel, key);
        lock (state)
        {
            state.Subscriptions.Remove(group);
        }
        await Groups.RemoveFromGroupAsync(Context.ConnectionId, group);
    }

    /// <summary>Publish to a channel's subscribers, where the channel lets browsers publish.</summary>
    public async Task Publish(string channel, string? key, JsonElement payload)
    {
        var state = State;
        var definition = await ChannelAsync(channel);
        Refuse(Channels.CheckKey(definition, key));
        Refuse(Access.CheckPublish(definition, state, key));
        lock (state)
        {
            var now = DateTimeOffset.UtcNow;
            if (now - state.WindowStart > PublishWindow)
            {
                state.WindowStart = now;
                state.WindowCount = 0;
            }
            if (++state.WindowCount > PublishesPerWindow)
            {
                throw new HubException("Too many messages: slow down.");
            }
        }
        if (Channels.CheckPayload(definition, payload) is { } problems)
        {
            throw new HubException("The payload does not match the channel: " + string.Join("; ", problems));
        }

        var group = ChannelGroups.Channel(state.TenantId, state.EnvironmentId, state.Slug, channel, key);
        await Clients.Group(group).SendAsync(MessageMethod, channel, key, payload, state.UserId);

        if (definition.RaiseEvent)
        {
            await RaiseAsync(state, new WebSocketMessageReceived(state.InstanceId, state.Slug, channel, key, payload, state.UserId));
        }
    }

    /// <summary>The channel as the instance declares it now; refuses a disabled instance or an unknown channel.</summary>
    private async Task<ChannelDefinition> ChannelAsync(string channel)
    {
        var state = State;
        if (!Channels.NamePattern().IsMatch(channel))
        {
            throw new HubException("Unknown channel.");
        }
        using var scope = scopes.CreateScope();
        using var rls = RlsScope.Environment(state.TenantId, state.EnvironmentId);
        var factory = scope.ServiceProvider.GetRequiredService<PluginContextFactory>();
        var instance = (await factory.EnabledInstancesAsync(state.TenantId, Context.ConnectionAborted))
            .FirstOrDefault(i => i.InstanceId == state.InstanceId);
        if (instance is null)
        {
            Context.Abort();
            throw new HubException("This realtime service is no longer available.");
        }
        return Channels.Find(instance.Config.RootElement, channel) ?? throw new HubException("Unknown channel.");
    }

    /// <summary>Best effort: delivery already happened, and a bus problem must never cost a message.</summary>
    private async Task RaiseAsync(ConnectionState state, WebSocketMessageReceived message)
    {
        try
        {
            using var scope = scopes.CreateScope();
            using var rls = RlsScope.Environment(state.TenantId, state.EnvironmentId);
            var factory = scope.ServiceProvider.GetRequiredService<PluginContextFactory>();
            var instance = (await factory.EnabledInstancesAsync(state.TenantId, CancellationToken.None))
                .FirstOrDefault(i => i.InstanceId == state.InstanceId);
            var plugin = await factory.CreateAsync(state.TenantId, WebSocketsPlugin.PluginId, instance, PluginActor.Anonymous, CancellationToken.None);
            await plugin.PublishAsync(message, CancellationToken.None);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Raising websockets.message.received failed (delivery already sent).");
        }
    }

    private static void Refuse(string? problem)
    {
        if (problem is not null)
        {
            throw new HubException(problem);
        }
    }
}

/// <summary>What one connection is, fixed at connect, plus its subscriptions and publish window.</summary>
public sealed class ConnectionState(Guid tenantId, Guid environmentId, Guid instanceId, string slug)
{
    public Guid TenantId { get; } = tenantId;
    public Guid EnvironmentId { get; } = environmentId;
    public Guid InstanceId { get; } = instanceId;
    public string Slug { get; } = slug;

    /// <summary>The signed-in site user or visitor; null when anonymous.</summary>
    public Guid? UserId { get; set; }

    /// <summary>
    /// The caller's channel site permissions, read at connect.
    /// ponytail: a role change applies on the next connect; push a refresh if that ever matters.
    /// </summary>
    public IReadOnlySet<string> Permissions { get; set; } = new HashSet<string>();

    public HashSet<string> Subscriptions { get; } = new(StringComparer.Ordinal);
    public DateTimeOffset WindowStart { get; set; } = DateTimeOffset.MinValue;
    public int WindowCount { get; set; }
}

/// <summary>Whether a connection may subscribe to or publish on a channel. Null means allowed.</summary>
public static class Access
{
    public static string? CheckSubscribe(ChannelDefinition channel, ConnectionState caller, string? key) => channel.Subscribe switch
    {
        SubscribeAccess.Public => null,
        SubscribeAccess.SignedIn => caller.UserId is null ? "Sign in to subscribe to this channel." : null,
        SubscribeAccess.Permission => caller.Permissions.Contains(ChannelPermissions.Subscribe(caller.Slug, channel.Name))
            ? null
            : "You may not subscribe to this channel.",
        SubscribeAccess.Self => caller.UserId is { } id && Guid.TryParse(key, out var k) && k == id
            ? null
            : "This channel is private to each user: subscribe with your own id as the key.",
        _ => "You may not subscribe to this channel.",
    };

    public static string? CheckPublish(ChannelDefinition channel, ConnectionState caller, string? key) => channel.Publish switch
    {
        PublishAccess.Subscribers => CheckSubscribe(channel, caller, key) is null ? null : "You may not publish on this channel.",
        PublishAccess.Permission => caller.Permissions.Contains(ChannelPermissions.Publish(caller.Slug, channel.Name))
            ? null
            : "You may not publish on this channel.",
        _ => "Only the server publishes on this channel.",
    };
}

/// <summary>
/// SignalR group names. Every send is scoped by tenant, environment and instance, so no name a
/// browser picks can reach another tenant's, environment's or instance's subscribers.
/// </summary>
public static class ChannelGroups
{
    public static string Channel(Guid tenant, Guid environment, string slug, string channel, string? key) =>
        key is null ? $"ws:{tenant:N}:{environment:N}:{slug}:c:{channel}" : $"ws:{tenant:N}:{environment:N}:{slug}:c:{channel}/{key}";

    public static string User(Guid tenant, Guid environment, string slug, Guid user) =>
        $"ws:{tenant:N}:{environment:N}:{slug}:u:{user:N}";
}

/// <summary>Site permission keys (users.access@1): <c>websockets:{instance}:channel:{name}:{action}</c>.</summary>
public static class ChannelPermissions
{
    public static string Prefix(string slug) => $"{WebSocketsPlugin.PluginId}:{slug}:channel:";
    public static string Subscribe(string slug, string channel) => $"{Prefix(slug)}{channel}:subscribe";
    public static string Publish(string slug, string channel) => $"{Prefix(slug)}{channel}:publish";
}

/// <summary>Operator settings, <c>Plugins:websockets:*</c>.</summary>
public sealed class WebSocketsOptions
{
    /// <summary>Open connections one tenant may hold on one content-api replica.</summary>
    public int MaxConnectionsPerTenant { get; set; } = 2000;
}
