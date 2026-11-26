using Dcms.Plugins.WebSockets.Api;
using Dcms.PluginSdk.Abstractions.Contracts;
using Microsoft.AspNetCore.SignalR;

namespace Dcms.Plugins.WebSockets;

/// <summary>
/// <c>websockets.channels@1</c>: sends from functions and plugins. The same on every plane —
/// <see cref="IHubContext{THub}"/> publishes on the hub's own backplane, and every content-api
/// replica delivers to the connections it holds. Server sends ignore the channel's publish rule
/// (that rule is about browsers) but never its schema.
/// </summary>
public sealed class ChannelSender(IPluginContext context, IHubContext<WebSocketsHub> hub) : IWebSocketChannels
{
    public Task SendAsync(ChannelMessage input, CancellationToken ct)
    {
        var (instance, channel) = Resolve(input.Channel);
        if (Channels.CheckKey(channel, input.Key) is { } keyProblem)
        {
            throw new ContractValidationException(keyProblem);
        }
        Validate(channel, input.Payload);
        var group = ChannelGroups.Channel(instance.TenantId, instance.EnvironmentId, instance.Slug, channel.Name, input.Key);
        return hub.Clients.Group(group).SendAsync(WebSocketsHub.MessageMethod, channel.Name, input.Key, input.Payload, null, ct);
    }

    public Task SendToUserAsync(DirectMessage input, CancellationToken ct)
    {
        var (instance, channel) = Resolve(input.Channel);
        Validate(channel, input.Payload);
        var group = ChannelGroups.User(instance.TenantId, instance.EnvironmentId, instance.Slug, input.To);
        return hub.Clients.Group(group).SendAsync(WebSocketsHub.MessageMethod, channel.Name, null, input.Payload, null, ct);
    }

    private (Dcms.PluginSdk.Abstractions.PluginInstanceContext Instance, ChannelDefinition Channel) Resolve(string name)
    {
        var instance = context.Instance ?? throw new InvalidOperationException("WebSockets channels are per instance.");
        var channel = Channels.Find(instance.Config.RootElement, name)
            ?? throw new ContractValidationException($"Instance '{instance.Slug}' has no channel '{name}'.");
        return (instance, channel);
    }

    private static void Validate(ChannelDefinition channel, System.Text.Json.JsonElement payload)
    {
        if (Channels.CheckPayload(channel, payload) is { } problems)
        {
            throw new ContractValidationException($"The payload does not match channel '{channel.Name}': " + string.Join("; ", problems));
        }
    }
}
