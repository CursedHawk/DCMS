using Dcms.Plugins.WebSockets.Api;
using Dcms.PluginSdk.Abstractions.Contracts;
using Microsoft.AspNetCore.SignalR;

namespace Dcms.Plugins.WebSockets;

/// <summary>
/// <see cref="IWebSocketPush"/>: a plugin's sends to its own channel set (ADR 0037), on the same
/// groups and backplane as <see cref="ChannelSender"/>, checked against the set's schemas.
/// </summary>
public sealed class ChannelPush(IEnumerable<WebSocketChannelSet> sets, IHubContext<WebSocketsHub> hub) : IWebSocketPush
{
    public Task SendAsync(string pluginId, Guid tenantId, Guid environmentId, string instanceSlug, ChannelMessage message, CancellationToken ct)
    {
        var channel = sets.FirstOrDefault(s => s.PluginId == pluginId)?.Channels.FirstOrDefault(c => c.Name == message.Channel)
            ?? throw new ContractValidationException($"Plugin '{pluginId}' declares no channel '{message.Channel}'.");
        if (Channels.CheckKey(channel, message.Key) is { } keyProblem)
        {
            throw new ContractValidationException(keyProblem);
        }
        if (Channels.CheckPayload(channel, message.Payload) is { } problems)
        {
            throw new ContractValidationException($"The payload does not match channel '{channel.Name}': " + string.Join("; ", problems));
        }
        var group = ChannelGroups.Channel(tenantId, environmentId, instanceSlug, channel.Name, message.Key);
        return hub.Clients.Group(group).SendAsync(WebSocketsHub.MessageMethod, channel.Name, message.Key, message.Payload, null, ct);
    }
}
