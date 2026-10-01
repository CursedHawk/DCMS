using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Dcms.PluginSdk.Abstractions;
using Dcms.PluginSdk.Abstractions.Contracts;

namespace Dcms.PluginSdk.Runtime;

/// <summary>
/// A plugin described for someone building against it: every contract it provides (operations
/// with input/output schemas, events, hooks), the data it keeps, what it needs and listens to,
/// its configuration, which package to reference, and C# that uses it. Built from the registry
/// alone, so it is always what the running code actually does.
/// </summary>
public static class PluginReference
{
    public sealed record Package(string Id, string Purpose);

    public sealed record OperationInfo(
        string Name, string Method, string Risk, string? Permission, IReadOnlyList<string> Exposed,
        bool ReturnsExternalText, string? Description, string? InputType, string OutputType,
        JsonNode InputSchema, JsonNode? OutputSchema);

    public sealed record NamedSchema(string Name, string ClrType, JsonNode Schema);

    public sealed record ContractInfo(
        string Id, string? Description, string ClrType, string Assembly, IReadOnlyList<string> Providers,
        IReadOnlyList<OperationInfo> Operations, IReadOnlyList<NamedSchema> Events, IReadOnlyList<NamedSchema> Hooks);

    public sealed record Requirement(string ContractId, bool Optional, string? BindingConfigKey, IReadOnlyList<string> Providers);

    public sealed record ContentTypeInfo(
        string Name, bool Searchable, IReadOnlyList<object> Fields, string? PublishedEvent, string? UnpublishedEvent);

    public sealed record Reference(
        string Id, string Name, string Version, string Description, string Source, int SdkMajor,
        IReadOnlyList<Package> Packages,
        IReadOnlyList<ContractInfo> Provides,
        IReadOnlyList<Requirement> Consumes,
        IReadOnlyList<string> Subscribes,
        IReadOnlyList<object> Intercepts,
        IReadOnlyList<object> Jobs,
        IReadOnlyList<ContentTypeInfo> ContentTypes,
        JsonNode Config,
        IReadOnlyList<string> PublicConfigKeys,
        IReadOnlyList<object> Permissions,
        IReadOnlyList<object> DataSets,
        string CSharp);

    public static Reference? Build(PluginRegistry registry, string pluginId)
    {
        var manifest = registry.Find(pluginId);
        if (manifest is null)
        {
            return null;
        }

        var provides = (manifest.Provides ?? [])
            .Select(p => registry.FindContract(p.Contract)!)
            .Select(c => Describe(c))
            .ToList();

        var packages = provides.Select(c => c.Assembly).Distinct(StringComparer.Ordinal)
            .Select(a => new Package(a, "Reference this to call the plugin, handle its events or intercept its hooks."))
            .Prepend(new Package(typeof(IPlugin).Assembly.GetName().Name!, "The plugin SDK: every plugin builds against it."))
            .ToList();

        var consumes = (manifest.Consumes ?? []).Select(r => new Requirement(
            r.ContractId, r.Optional, r.BindingConfigKey,
            registry.FindContract(r.ContractId)?.ProviderPluginIds.ToList() ?? [])).ToList();

        JsonNode config;
        try
        {
            config = JsonNode.Parse(manifest.ConfigJsonSchema) ?? new JsonObject();
        }
        catch (JsonException)
        {
            config = new JsonObject();
        }

        return new Reference(
            manifest.Id, manifest.Name, manifest.Version, manifest.Description, registry.SourceOf(manifest.Id),
            Hosting.PluginLoader.SdkMajor,
            packages,
            provides,
            consumes,
            (manifest.Subscribes ?? []).Select(s => s.EventName).ToList(),
            (manifest.Intercepts ?? []).Select(i => (object)new { hook = i.HookName, i.Priority }).ToList(),
            (manifest.Jobs ?? []).Select(j => (object)new { j.Name, intervalMinutes = j.Interval?.TotalMinutes }).ToList(),
            manifest.ContentTypes.Select(t => new ContentTypeInfo(
                t.Name, t.Searchable,
                t.Fields.Select(f => (object)new { f.Name, type = f.Type.ToString(), f.Required, f.Description }).ToList(),
                t.Published is null ? null : ContractIds.EventName(t.Published),
                t.Unpublished is null ? null : ContractIds.EventName(t.Unpublished))).ToList(),
            config,
            manifest.PublicConfigKeys,
            manifest.Permissions.Select(p => (object)new
            {
                key = PluginPermissions.Resolve(manifest.Id, p.Action), p.DisplayName, p.Description, p.GrantToMembers,
            }).ToList(),
            Data.PluginDataSets.Of(manifest).Select(d => (object)new
            {
                d.Id, d.Title, d.Description,
                readPermission = Data.PluginDataEndpoints.ReadPermission(manifest.Id, d),
                writePermission = Data.PluginDataEndpoints.WritePermission(manifest.Id, d),
            }).ToList(),
            CSharp(manifest, provides, registry));
    }

    public static ContractInfo Describe(RegisteredContract contract)
    {
        var d = contract.Descriptor;
        return new ContractInfo(
            d.Id, d.Description, d.ClrType, d.Assembly, contract.ProviderPluginIds.ToList(),
            d.Operations.Select(o => new OperationInfo(
                o.Name, o.Method.Name, o.Risk.ToString().ToLowerInvariant(), o.Permission, Exposed(o.Expose), o.ReturnsExternalText, o.Description,
                o.InputType is null ? null : TypeName(o.InputType),
                o.OutputType is null ? "Task" : $"Task<{TypeName(o.OutputType)}>",
                o.InputSchema, o.OutputSchema)).ToList(),
            d.Events.Select(e => new NamedSchema(e.Name, TypeName(e.EventType), e.Schema)).ToList(),
            d.Hooks.Select(h => new NamedSchema(h.Name, TypeName(h.HookType), h.Schema)).ToList());
    }

    private static List<string> Exposed(OpExposure expose)
    {
        var planes = new List<string> { "plugins" };
        if (expose.HasFlag(OpExposure.Site)) planes.Add("site");
        if (expose.HasFlag(OpExposure.Admin)) planes.Add("admin");
        if (expose.HasFlag(OpExposure.Ai)) planes.Add("ai");
        return planes;
    }

    /// <summary>C# spelling of a type: generic arguments, nullable annotations left to the reader.</summary>
    public static string TypeName(Type type)
    {
        if (Nullable.GetUnderlyingType(type) is { } inner)
        {
            return TypeName(inner) + "?";
        }
        if (!type.IsGenericType)
        {
            return type.Name;
        }
        var name = type.Name[..type.Name.IndexOf('`')];
        return $"{name}<{string.Join(", ", type.GetGenericArguments().Select(TypeName))}>";
    }

    /// <summary>A starting point for a consumer: the manifest lines and one call of each kind.</summary>
    private static string CSharp(PluginManifest manifest, IReadOnlyList<ContractInfo> provides, PluginRegistry registry)
    {
        if (provides.Count == 0)
        {
            return $"// {manifest.Name} offers no contracts: other plugins cannot call it.";
        }

        var sb = new StringBuilder();
        foreach (var assembly in provides.Select(p => p.Assembly).Distinct())
        {
            sb.AppendLine($"// dotnet add package {assembly}");
        }
        sb.AppendLine($"using {provides[0].ClrType[..provides[0].ClrType.LastIndexOf('.')]};");
        sb.AppendLine();
        sb.AppendLine("// In your manifest:");
        sb.AppendLine($"consumes: [{string.Join(", ", provides.Select(p => $"ContractRequirement.Of<{Short(p.ClrType)}>()"))}],");

        var events = provides.SelectMany(p => p.Events).ToList();
        if (events.Count > 0)
        {
            sb.AppendLine($"subscribes: [EventSubscription.Of<{events[0].ClrType}, MyHandler>()],");
        }
        var hooks = provides.SelectMany(p => p.Hooks).ToList();
        if (hooks.Count > 0)
        {
            sb.AppendLine($"intercepts: [HookSubscription.Of<{hooks[0].ClrType}, MyInterceptor>(priority: 0)],");
        }

        sb.AppendLine();
        sb.AppendLine("// In a route, job or handler (IPluginContext context):");
        foreach (var contract in provides)
        {
            var variable = char.ToLowerInvariant(Short(contract.ClrType)[1]) + Short(contract.ClrType)[2..];
            sb.AppendLine($"var {variable} = context.Contracts.Get<{Short(contract.ClrType)}>();");
            foreach (var op in contract.Operations)
            {
                var args = op.InputType is null ? "ct" : $"new {op.InputType}(/* … */), ct";
                sb.AppendLine($"await {variable}.{op.Method}({args});   // → {op.OutputType}");
            }
        }

        if (events.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine($"public sealed class MyHandler : IPluginEventHandler<{events[0].ClrType}>");
            sb.AppendLine("{");
            sb.AppendLine($"    public Task HandleAsync({events[0].ClrType} e, IPluginContext context, CancellationToken ct) => Task.CompletedTask;");
            sb.AppendLine("}");
        }
        if (hooks.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine($"public sealed class MyInterceptor : IPluginHookHandler<{hooks[0].ClrType}>");
            sb.AppendLine("{");
            sb.AppendLine($"    public ValueTask<HookResult<{hooks[0].ClrType}>> HandleAsync({hooks[0].ClrType} hook, IPluginContext context, CancellationToken ct) =>");
            sb.AppendLine("        ValueTask.FromResult(HookResult.Continue(hook));");
            sb.AppendLine("}");
        }
        return sb.ToString();

        static string Short(string clrType) => clrType[(clrType.LastIndexOf('.') + 1)..];
    }
}
