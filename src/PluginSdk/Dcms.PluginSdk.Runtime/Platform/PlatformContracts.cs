using Dcms.PluginSdk.Abstractions;
using Dcms.PluginSdk.Abstractions.Contracts;
using Dcms.PluginSdk.Abstractions.Platform;

namespace Dcms.PluginSdk.Runtime.Platform;

public static class PlatformContracts
{
    /// <summary>
    /// Registers the <c>dcms.*</c> platform contracts for a host. Both planes register every
    /// contract id so plugin manifests validate identically in either; where a capability must
    /// not exist on the public plane (<c>dcms.secrets@1</c>), the site plane gets an
    /// implementation that refuses.
    /// </summary>
    public static PluginRegistryBuilder AddPlatformContracts(this PluginRegistryBuilder builder, PluginPlane plane)
    {
        builder.AddPlatformContract<IPluginStorage, PluginStorage>();
        builder.AddPlatformContract<IPluginBlobs, PluginBlobs>();
        builder.AddPlatformContract<IPluginCache, PluginCache>();
        builder.AddPlatformContract<IPluginEmail, PluginEmail>();
        builder.AddPlatformContract<IPluginNotifications, PluginNotifications>();
        builder.AddPlatformContract<IPluginContent, PluginContent>();
        builder.AddPlatformContract<IPluginMedia, PluginMedia>();
        builder.AddPlatformContract<IPluginSearch, PluginSearch>();
        builder.AddPlatformContract<IPluginAi, PluginAi>();
        builder.AddPlatformContract<IPluginEvents, PluginEvents>();
        builder.AddPlatformContract<IPluginJobs, PluginJobs>();

        if (plane == PluginPlane.Admin)
        {
            builder.AddPlatformContract<IPluginSecrets, PluginSecrets>();
        }
        else
        {
            builder.AddPlatformContract<IPluginSecrets, SitePlaneSecrets>();
        }
        return builder;
    }

    private sealed class SitePlaneSecrets : IPluginSecrets
    {
        private static InvalidOperationException Refused() =>
            new("dcms.secrets@1 is not available on the public plane; spend credentials from an admin route, job or event handler.");

        public Task<SecretValue> GetAsync(SecretName input, CancellationToken ct) => throw Refused();
        public Task SetAsync(SetSecret input, CancellationToken ct) => throw Refused();
        public Task<DeleteResult> DeleteAsync(SecretName input, CancellationToken ct) => throw Refused();
        public Task<SecretList> ListAsync(SecretNames input, CancellationToken ct) => throw Refused();
    }
}
