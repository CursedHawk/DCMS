using Dcms.Plugins.UserAuth.Api;
using Dcms.PluginSdk.Abstractions.Contracts;

namespace Dcms.Plugins.WebSockets;

/// <summary>
/// What a tenant can give its site users on this instance (users.resources@1): subscribing to and
/// publishing on each channel whose access is <c>permission</c>. The keys are
/// <c>websockets:{slug}:channel:{name}:{subscribe|publish}</c>; renaming a channel renames them.
/// </summary>
public sealed class ChannelUserResources(IPluginContext context) : IUserResources
{
    public Task<UserResourceCatalog> ListAsync(CancellationToken ct)
    {
        var instance = context.Instance ?? throw new InvalidOperationException("WebSockets resources are per instance.");
        var resources = Channels.Read(instance.Config.RootElement)
            .Where(c => c.Subscribe == Api.SubscribeAccess.Permission || c.Publish == Api.PublishAccess.Permission)
            .Select(c => new UserResource($"channel:{c.Name}", c.Description ?? c.Name,
            [
                .. c.Subscribe == Api.SubscribeAccess.Permission ? [new UserResourceAction("subscribe", "Subscribe")] : Array.Empty<UserResourceAction>(),
                .. c.Publish == Api.PublishAccess.Permission ? [new UserResourceAction("publish", "Publish")] : Array.Empty<UserResourceAction>(),
            ]))
            .ToList();
        return Task.FromResult(new UserResourceCatalog(WebSocketsPlugin.PluginId, instance.Slug, instance.Name, resources));
    }
}
