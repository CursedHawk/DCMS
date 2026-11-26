using System.Text.Json;
using System.Text.Json.Nodes;
using Dcms.Plugins.WebSockets.Api;
using Dcms.PluginSdk.Abstractions;
using Dcms.PluginSdk.Abstractions.Contracts;
using Dcms.PluginSdk.Abstractions.Platform;
using Microsoft.AspNetCore.Http;

namespace Dcms.Plugins.WebSockets;

/// <summary>
/// Editing an instance's channels: <c>websockets.channel-admin@1</c> for the assistant, and the admin
/// routes the dcms CLI and the web IDE use. Every write goes through <c>dcms.plugins@1</c>, so it
/// gets what the console's settings form gets: the config schema (which holds payload schemas to
/// the supported subset), the deployment-managed refusal and the audit record.
/// </summary>
public sealed class ChannelAdmin(IPluginContext context) : IWebSocketChannelAdmin
{
    private PluginInstanceContext Instance =>
        context.Instance ?? throw new InvalidOperationException("WebSockets channels are per instance.");

    public Task<ChannelList> ListAsync(ChannelListQuery input, CancellationToken ct) =>
        Task.FromResult(new ChannelList(Channels.Read(Instance.Config.RootElement)));

    public async Task<ChannelDefinition> PutAsync(ChannelDefinition input, CancellationToken ct)
    {
        var channels = Channels.Read(Instance.Config.RootElement).ToList();
        var at = channels.FindIndex(c => c.Name == input.Name);
        if (at >= 0) channels[at] = input;
        else channels.Add(input);
        await SaveAsync(channels, ct);
        return input;
    }

    public async Task DeleteAsync(ChannelRef input, CancellationToken ct)
    {
        var channels = Channels.Read(Instance.Config.RootElement).ToList();
        if (channels.RemoveAll(c => c.Name == input.Name) == 0)
        {
            throw new ContractValidationException($"There is no channel '{input.Name}'.");
        }
        await SaveAsync(channels, ct);
    }

    /// <summary>Replaces the whole channel list, as the CLI's push and the IDE's save do.</summary>
    public async Task<ChannelList> ReplaceAsync(IReadOnlyList<ChannelDefinition> channels, CancellationToken ct)
    {
        var duplicate = channels.GroupBy(c => c.Name).FirstOrDefault(g => g.Count() > 1);
        if (duplicate is not null)
        {
            throw new ContractValidationException($"Channel '{duplicate.Key}' is declared twice.");
        }
        await SaveAsync(channels, ct);
        return new ChannelList(channels);
    }

    private async Task SaveAsync(IEnumerable<ChannelDefinition> channels, CancellationToken ct)
    {
        var instance = Instance;
        var config = Channels.With(instance.Config.RootElement, channels);
        await context.Contracts.Get<IPluginInstances>().UpdateSettingsAsync(new PluginSettingsUpdate(instance.Slug, Config: config), ct);
    }

    /// <summary>
    /// The JSON Schema of a channels file (<c>{ "channels": [...] }</c>): the config schema, plus the
    /// <c>$schema</c> key an editor reads. What the IDE attaches to its document and the CLI writes
    /// beside the file, so VS Code and Monaco check the same subset the server does.
    /// </summary>
    public static JsonNode FileSchema()
    {
        var schema = JsonNode.Parse(WebSocketsPlugin.ConfigSchema)!.AsObject();
        schema["properties"]!.AsObject()["$schema"] = new JsonObject { ["type"] = "string" };
        return schema;
    }

    internal static void Map(IPluginEndpointBuilder endpoints)
    {
        // The channels file: what set-channels takes back, so `dcms op call … -o channels.json` round-trips.
        endpoints.MapGet("/channels", (IPluginContext plugin) =>
                Results.Ok(new { channels = Channels.Read(plugin.Instance!.Config.RootElement) }))
            .RequirePluginPermission("manage")
            .Cli("channels", "The instance's realtime channels and their payload JSON Schemas, as a channels file.", OpRisk.Read);

        // What a channels file is checked against: point its "$schema" at a saved copy and an editor checks it.
        endpoints.MapGet("/channels/schema", () => Results.Json(FileSchema()))
            .RequirePluginPermission("manage")
            .Cli("channels-schema", "The JSON Schema of a channels file, for an editor's \"$schema\".", OpRisk.Read);

        endpoints.MapPut("/channels", async (ChannelsFile body, IPluginContext plugin, CancellationToken ct) =>
                Results.Ok(await new ChannelAdmin(plugin).ReplaceAsync(body.Channels ?? [], ct)))
            .RequirePluginPermission("manage")
            .AuditAs("channels.replace")
            .Cli("set-channels", "Replace the instance's realtime channels with a channels file's.", OpRisk.Safe);
    }

    /// <summary>A channels file, as the CLI and the IDE send it; <c>$schema</c> is the editor's and is dropped.</summary>
    public sealed record ChannelsFile(IReadOnlyList<ChannelDefinition>? Channels)
    {
        [System.Text.Json.Serialization.JsonPropertyName("$schema")]
        public string? Schema { get; init; }
    }
}
