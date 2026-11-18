using System.Text.Json;
using Dcms.PluginSdk.Abstractions;
using Dcms.PluginSdk.Abstractions.Contracts;
using Dcms.PluginSdk.Abstractions.Platform;
using Dcms.PluginSdk.Runtime;
using Dcms.Shared.Data.Cms;
using Microsoft.EntityFrameworkCore;

namespace Dcms.AdminApi.Plugins;

/// <summary>
/// <c>dcms.plugins@1</c>: the console's plugin instance routes as a contract, for the assistant.
/// Every change goes through <see cref="PluginInstanceService"/>, as the routes do.
///
/// <para><b>On the AI plane an existing instance is reachable only once its AI tools are on</b> —
/// the same switch that gates the instance's own contracts — and nothing here can turn it on.
/// Installing is not gated: a new instance starts with its AI tools off.</para>
/// </summary>
public sealed class AdminPluginInstances(
    IPluginContext context, PluginInstanceService plugins, CmsDbContext db, IPluginCatalog catalog, PluginRegistry registry) : IPluginInstances
{
    /// <summary>The caller name the dispatcher gives a platform contract on the AI plane.</summary>
    private const string AiCaller = "ai";

    public async Task<PluginOverview> ListAsync(CancellationToken ct)
    {
        var installed = await db.PluginInstances.AsNoTracking().OrderBy(p => p.Slug).ToListAsync(ct);
        var available = catalog.Manifests.OrderBy(m => m.Name).Select(m => new AvailablePlugin(
                m.Id, m.Name, m.Summary ?? m.Description, m.Category ?? "Other", m.AllowMultipleInstances, HasSettings(m),
                PluginDependencies.Of(registry, m.Id).Where(d => !d.Optional).Select(d => string.Join(" or ", d.Providers)).ToList()))
            .ToList();
        return new PluginOverview(available, installed.Select(ToInstalled).ToList());
    }

    public async Task<PluginSettings> GetSettingsAsync(PluginInstanceRef input, CancellationToken ct) =>
        ToSettings(await FindAsync(input.Instance, ct));

    public async Task<PluginSettings> UpdateSettingsAsync(PluginSettingsUpdate input, CancellationToken ct)
    {
        var instance = await FindAsync(input.Instance, ct);
        if (input.Config is { ValueKind: not JsonValueKind.Object and not JsonValueKind.Null })
        {
            throw new ContractValidationException("config is a JSON object: the instance's whole configuration.");
        }
        var config = input.Config is { ValueKind: JsonValueKind.Object } given ? given.GetRawText() : null;
        return ToSettings(await Change(() => plugins.UpdateAsync(instance.Id, input.Name, input.Description, config, aiToolsEnabled: null, ct)));
    }

    public async Task<InstalledPlugin> InstallAsync(PluginInstall input, CancellationToken ct)
    {
        if (context.PluginId == AiCaller && input.Config is { ValueKind: not JsonValueKind.Null })
        {
            // The opt-in gate covers every configuration, a new instance's first one too.
            throw new ContractValidationException(
                "Install with the default settings; once the operator has turned on the instance's AI tools, change them with UpdateSettings.");
        }
        if (input.Config is { ValueKind: not JsonValueKind.Object and not JsonValueKind.Null })
        {
            throw new ContractValidationException("config is a JSON object.");
        }
        var config = input.Config is { ValueKind: JsonValueKind.Object } given ? given.GetRawText() : null;
        return ToInstalled(await Change(async () => (PluginInstance?)await plugins.CreateAsync(input.PluginId, input.Slug, input.Name, input.Description, config, ct)));
    }

    public async Task<InstalledPlugin> SetEnabledAsync(PluginEnabledChange input, CancellationToken ct)
    {
        var instance = await FindAsync(input.Instance, ct);
        return ToInstalled(await Change(() => plugins.SetEnabledAsync(instance.Id, input.Enabled, ct)));
    }

    /// <summary>The instance by slug or id — on the AI plane, only one a person opted in to AI tools.</summary>
    private async Task<PluginInstance> FindAsync(string reference, CancellationToken ct)
    {
        var id = Guid.TryParse(reference, out var parsed) ? parsed : (Guid?)null;
        var instance = await db.PluginInstances.AsNoTracking().FirstOrDefaultAsync(p => p.Slug == reference || p.Id == id, ct)
                       ?? throw new ContractValidationException($"No plugin instance '{reference}'; list the plugins first.");
        if (context.PluginId == AiCaller && !instance.AiToolsEnabled)
        {
            throw new ContractAccessDeniedException(
                $"AI tools are off for '{instance.Name}' ({instance.Slug}). Ask the operator to turn them on in its Settings; only a person can.");
        }
        return instance;
    }

    private static async Task<PluginInstance> Change(Func<Task<PluginInstance?>> change)
    {
        try
        {
            return await change() ?? throw new ContractValidationException("The plugin instance no longer exists.");
        }
        catch (PluginInstanceRefusal refusal)
        {
            throw refusal.Status == StatusCodes.Status409Conflict
                ? new ContractConflictException(refusal.Message)
                : new ContractValidationException(refusal.Message);
        }
    }

    private PluginSettings ToSettings(PluginInstance instance)
    {
        var schema = catalog.Find(instance.PluginId)?.ConfigJsonSchema ?? "{}";
        return new PluginSettings(ToInstalled(instance), JsonDocument.Parse(schema).RootElement.Clone(),
            JsonDocument.Parse(string.IsNullOrWhiteSpace(instance.ConfigJson) ? "{}" : instance.ConfigJson).RootElement.Clone());
    }

    private static InstalledPlugin ToInstalled(PluginInstance p) =>
        new(p.Id, p.PluginId, p.Slug, p.Name, p.Description, p.Enabled, p.AiToolsEnabled);

    private static bool HasSettings(PluginManifest manifest)
    {
        using var schema = JsonDocument.Parse(string.IsNullOrWhiteSpace(manifest.ConfigJsonSchema) ? "{}" : manifest.ConfigJsonSchema);
        return schema.RootElement.TryGetProperty("properties", out var properties)
               && properties.ValueKind == JsonValueKind.Object && properties.EnumerateObject().Any();
    }
}
