using System.Text.Json;
using Dcms.PluginSdk.Abstractions;
using Dcms.PluginSdk.Abstractions.Contracts;

namespace Dcms.Plugins.WebSockets.Api;

/// <summary>The WebSockets plugin's permissions, held per instance (<c>plugin:websockets:{slug}:{action}</c>).</summary>
public static class WebSocketsPermissions
{
    /// <summary>Send to a channel or to one user's connections (functions vouch for it through <c>uses</c>).</summary>
    public const string Send = "plugin:websockets:send";

    /// <summary>Run on what browsers publish: the <c>on.websocket</c> function trigger.</summary>
    public const string Read = "plugin:websockets:read";

    /// <summary>Add, change and remove channels.</summary>
    public const string Manage = "plugin:websockets:manage";
}

/// <summary>Who may subscribe to a channel from a browser.</summary>
public static class SubscribeAccess
{
    /// <summary>Anyone, signed in or not.</summary>
    public const string Public = "public";

    /// <summary>A signed-in site user (User Authentication) or visitor (Visitor Authentication).</summary>
    public const string SignedIn = "signedIn";

    /// <summary>A signed-in user holding one of the channel's <c>subscribeRoles</c>.</summary>
    public const string Roles = "roles";

    /// <summary>A keyed channel whose key is the caller's own id: one private stream per user.</summary>
    public const string Self = "self";

    public static readonly string[] All = [Public, SignedIn, Roles, Self];
}

/// <summary>Who may publish to a channel from a browser. Functions and plugins always may.</summary>
public static class PublishAccess
{
    /// <summary>Only the server (functions, plugins) sends.</summary>
    public const string None = "none";

    /// <summary>Whoever may subscribe may also publish.</summary>
    public const string Subscribers = "subscribers";

    /// <summary>A signed-in user holding one of the channel's <c>publishRoles</c>.</summary>
    public const string Roles = "roles";

    public static readonly string[] All = [None, Subscribers, Roles];
}

/// <summary>
/// One channel of a WebSockets instance, as its config declares it.
/// </summary>
/// <param name="Name">URL-safe name; a keyed channel is subscribed to as <c>name/key</c>.</param>
/// <param name="Description">What the channel carries; shown in the generated clients' docs.</param>
/// <param name="Keyed">Whether the channel is split by a key (a chat room, an order id).</param>
/// <param name="Subscribe">Who may subscribe: one of <see cref="SubscribeAccess"/>.</param>
/// <param name="SubscribeRoles">Site roles for <see cref="SubscribeAccess.Roles"/>.</param>
/// <param name="Publish">Who may publish from a browser: one of <see cref="PublishAccess"/>.</param>
/// <param name="PublishRoles">Site roles for <see cref="PublishAccess.Roles"/>.</param>
/// <param name="RaiseEvent">Whether a browser publish is also raised as <see cref="WebSocketMessageReceived"/>.</param>
/// <param name="Schema">JSON Schema of the payload (the restricted subset); any JSON when absent.</param>
public sealed record ChannelDefinition(
    string Name,
    string? Description = null,
    bool Keyed = false,
    string Subscribe = SubscribeAccess.Public,
    IReadOnlyList<string>? SubscribeRoles = null,
    string Publish = PublishAccess.None,
    IReadOnlyList<string>? PublishRoles = null,
    bool RaiseEvent = false,
    JsonElement? Schema = null);

/// <summary>A site identity: a User Authentication user (<c>user</c>) or a Visitor Authentication visitor (<c>visitor</c>).</summary>
/// <param name="Kind"><c>user</c> or <c>visitor</c>.</param>
/// <param name="Id">The identity's subject id.</param>
public sealed record SiteIdentity(string Kind, string Id)
{
    public const string User = "user";
    public const string Visitor = "visitor";
}

/// <summary>Send a payload to every subscriber of a channel.</summary>
/// <param name="Channel">The channel's name.</param>
/// <param name="Key">The key, for a keyed channel; must be absent otherwise.</param>
/// <param name="Payload">The message; validated against the channel's schema.</param>
public sealed record ChannelMessage(string Channel, string? Key, JsonElement Payload);

/// <summary>Send a payload to every connection of one site identity, labelled with a channel.</summary>
/// <param name="To">Who receives it.</param>
/// <param name="Channel">The channel it is delivered as (its schema applies; the identity need not subscribe).</param>
/// <param name="Payload">The message; validated against the channel's schema.</param>
public sealed record DirectMessage(SiteIdentity To, string Channel, JsonElement Payload);

/// <summary>
/// A browser published on a channel that raises events. Carries the payload, which the channel's
/// schema already validated; the sender is absent for an anonymous connection.
/// </summary>
[ContractEvent("websockets.message.received")]
public sealed record WebSocketMessageReceived(
    Guid InstanceId, string Instance, string Channel, string? Key, JsonElement Payload, SiteIdentity? Sender) : IPluginEvent;

/// <summary>
/// Realtime delivery to a site's browsers. Nothing is stored: a message reaches the connections
/// that are open when it is sent.
/// </summary>
[DcmsContract("websockets.channels", 1,
    Description = "Send realtime messages to a site's browsers over a WebSockets instance's channels.",
    Events = [typeof(WebSocketMessageReceived)])]
public interface IWebSocketChannels
{
    // Safe, not Dangerous: delivery is ephemeral — nothing is stored or changed to undo.
    [Operation(OpRisk.Safe, Permission = WebSocketsPermissions.Send, Expose = OpExposure.Admin,
        Description = "Send a message to every subscriber of a channel (of one key, for a keyed channel).")]
    Task SendAsync(ChannelMessage input, CancellationToken ct);

    [Operation(OpRisk.Safe, Permission = WebSocketsPermissions.Send, Expose = OpExposure.Admin,
        Description = "Send a message to every open connection of one signed-in site user or visitor.")]
    Task SendToUserAsync(DirectMessage input, CancellationToken ct);
}

/// <summary>No input.</summary>
public sealed record ChannelListQuery;

/// <summary>A channel by name.</summary>
public sealed record ChannelRef(string Name);

/// <summary>The instance's channels.</summary>
public sealed record ChannelList(IReadOnlyList<ChannelDefinition> Channels);

/// <summary>
/// Editing a WebSockets instance's channels, for the assistant. A contract of its own, not more of
/// <see cref="IWebSocketChannels"/>: functions that send vouch for that contract's permissions, and
/// sending must not start to need — or grant — editing channels.
/// </summary>
[DcmsContract("websockets.channel-admin", 1, Description = "List, add, change and remove a WebSockets instance's channels.")]
public interface IWebSocketChannelAdmin
{
    [Operation(OpRisk.Read, Permission = WebSocketsPermissions.Manage, Expose = OpExposure.Ai,
        Description = "The instance's channels with their access rules and payload JSON Schemas.")]
    Task<ChannelList> ListAsync(ChannelListQuery input, CancellationToken ct);

    [Operation(OpRisk.Safe, Permission = WebSocketsPermissions.Manage, Expose = OpExposure.Ai,
        Description = "Add a channel, or replace the one of the same name. The schema must use the supported JSON Schema subset.")]
    Task<ChannelDefinition> PutAsync(ChannelDefinition input, CancellationToken ct);

    [Operation(OpRisk.Dangerous, Permission = WebSocketsPermissions.Manage, Expose = OpExposure.Ai,
        Description = "Remove a channel. Sites and functions that use it stop compiling against the regenerated client.")]
    Task DeleteAsync(ChannelRef input, CancellationToken ct);
}
