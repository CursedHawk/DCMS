using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using Dcms.PluginSdk.Abstractions;
using Dcms.PluginSdk.Abstractions.Contracts;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace Dcms.PluginSdk.Runtime.Contracts;

/// <summary>Where a call comes from, which decides the operations it may reach.</summary>
public enum ContractPlane
{
    /// <summary>The public site (content-api): anonymous callers and visitors; <see cref="OpExposure.Site"/> ops only.</summary>
    Site,

    /// <summary>Tenant members in the admin (admin-api); <see cref="OpExposure.Admin"/> ops.</summary>
    Admin,

    /// <summary>An AI agent acting for a member (admin-api); <see cref="OpExposure.Ai"/> ops.</summary>
    Ai,
}

/// <summary>One operation as the catalog describes it to a caller who may use it.</summary>
public sealed record CatalogOperation(
    string Name,
    string? Description,
    OpRisk Risk,
    string? Permission,
    bool ReturnsExternalText,
    JsonElement InputSchema,
    JsonElement? OutputSchema);

/// <summary>A contract available to this caller, with the instances that provide it.</summary>
public sealed record CatalogContract(
    string Id,
    string? Description,
    string? ProviderPluginId,
    IReadOnlyList<CatalogInstance> Instances,
    IReadOnlyList<CatalogOperation> Operations);

public sealed record CatalogInstance(Guid Id, string Slug, string Name, string Description);

/// <summary>
/// The one path by which anything outside the plugin runtime — a site's JavaScript, the admin
/// UI, an AI agent, and later an out-of-process plugin — invokes a contract operation. It takes
/// the transport-neutral form (contract id, operation name, JSON input) and enforces, in order:
/// the operation exists and is exposed on this plane (else 404, so unexposed operations cannot be
/// discovered), the caller holds its permission (403), the input deserialises into the
/// contract's own input type (400). Then it invokes through the same proxy plugins use, so the
/// call is traced and, when it writes, audited.
/// </summary>
public sealed class ContractDispatcher(PluginRegistry registry, PluginContextFactory factory, IServiceProvider services)
{
    /// <summary>
    /// Input is bound strictly: required properties must be present, non-nullable ones non-null,
    /// and unknown ones are refused rather than silently dropped.
    /// </summary>
    public static readonly JsonSerializerOptions Strict = new(ContractDescriptorBuilder.Json)
    {
        RespectNullableAnnotations = true,
        RespectRequiredConstructorParameters = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    };

    /// <summary>Everything this caller may invoke on this plane, for this tenant.</summary>
    public async Task<IReadOnlyList<CatalogContract>> CatalogAsync(
        Guid tenantId, ContractPlane plane, Func<string, Task<bool>> hasPermission, CancellationToken ct)
    {
        var enabled = await factory.EnabledInstancesAsync(tenantId, ct);
        var result = new List<CatalogContract>();
        foreach (var contract in registry.Contracts.OrderBy(c => c.Descriptor.Id, StringComparer.Ordinal))
        {
            var descriptor = contract.Descriptor;
            var instances = descriptor.IsPlatform
                ? []
                : enabled.Where(i => i.PluginId == descriptor.ProviderPluginId && (plane != ContractPlane.Ai || i.AiToolsEnabled))
                    .Select(i => new CatalogInstance(i.InstanceId, i.Slug, i.Name, i.Description)).ToList();
            if (!descriptor.IsPlatform && instances.Count == 0)
            {
                continue;
            }
            // Platform contracts are reachable from outside only where the platform says so,
            // never the site plane: they act "as the caller", and a site request is no plugin.
            if (descriptor.IsPlatform && plane == ContractPlane.Site)
            {
                continue;
            }

            var operations = new List<CatalogOperation>();
            foreach (var op in descriptor.Operations.Where(o => IsExposed(o, plane)))
            {
                if (op.Permission is { } permission && !await hasPermission(permission))
                {
                    continue; // absent, not disabled: see ToolSpec.permission in the admin agent
                }
                operations.Add(new CatalogOperation(
                    op.Name, op.Description, op.Risk, op.Permission, op.ReturnsExternalText,
                    JsonSerializer.SerializeToElement(op.InputSchema),
                    op.OutputSchema is null ? null : JsonSerializer.SerializeToElement(op.OutputSchema)));
            }
            if (operations.Count > 0)
            {
                result.Add(new CatalogContract(descriptor.Id, descriptor.Description, descriptor.ProviderPluginId, instances, operations));
            }
        }
        return result;
    }

    /// <param name="instance">The provider instance, by slug or id; optional when only one is enabled.</param>
    public async Task<IResult> InvokeAsync(
        Guid tenantId,
        string contractId,
        string operation,
        string? instance,
        JsonElement? input,
        ContractPlane plane,
        PluginActor actor,
        Func<string, Task<bool>> hasPermission,
        CancellationToken ct)
    {
        if (registry.FindContract(contractId) is not { } contract
            || contract.Descriptor.FindOperation(operation) is not { } op
            || !IsExposed(op, plane)
            || (contract.Descriptor.IsPlatform && plane == ContractPlane.Site))
        {
            return Results.NotFound();
        }
        if (op.Permission is { } permission && (plane == ContractPlane.Site || !await hasPermission(permission)))
        {
            return Results.Problem(statusCode: StatusCodes.Status403Forbidden, title: "Forbidden",
                detail: $"This operation requires the '{permission}' permission.");
        }

        IPluginContext context;
        if (contract.Descriptor.IsPlatform)
        {
            var enabled = await factory.EnabledInstancesAsync(tenantId, ct);
            context = new PluginContext(tenantId, PlatformCaller(plane), null, actor, enabled, registry, services);
        }
        else
        {
            var enabled = await factory.EnabledInstancesAsync(tenantId, ct);
            // On the AI plane only instances the tenant opted in to AI agents exist at all.
            var candidates = enabled
                .Where(i => i.PluginId == contract.Descriptor.ProviderPluginId && (plane != ContractPlane.Ai || i.AiToolsEnabled))
                .ToList();
            var provider = instance is { Length: > 0 }
                ? candidates.FirstOrDefault(i => i.Slug == instance || i.InstanceId.ToString() == instance)
                : candidates.Count == 1 ? candidates[0] : null;
            if (provider is null)
            {
                return candidates.Count > 1 && instance is null
                    ? Results.BadRequest(new { error = "Several instances provide this contract; name one with ?instance=." })
                    : Results.NotFound();
            }
            context = await factory.CreateAsync(tenantId, provider.PluginId, provider, actor, ct);
        }

        object? argument = null;
        if (op.InputType is { } inputType)
        {
            try
            {
                argument = (input ?? JsonSerializer.SerializeToElement(new { })).Deserialize(inputType, Strict)
                    ?? throw new JsonException("Input is required.");
            }
            catch (JsonException e)
            {
                return Results.BadRequest(new { error = $"Invalid input: {e.Message}" });
            }
        }

        var target = ContractActivator.Create(services, contract.Implementation, context);
        var proxy = typeof(ContractProxy).GetMethod(nameof(ContractProxy.Create))!
            .MakeGenericMethod(contract.Descriptor.ContractType)
            .Invoke(null, [target, contract.Descriptor, PlatformCaller(plane), services])!;

        try
        {
            var task = (Task)op.Method.Invoke(proxy, op.InputType is null ? [ct] : [argument, ct])!;
            await task;
            if (op.OutputType is null)
            {
                return Results.NoContent();
            }
            var value = task.GetType().GetProperty(nameof(Task<object>.Result))!.GetValue(task);
            return Results.Json(value, ContractDescriptorBuilder.Json);
        }
        catch (Exception e) when (Unwrap(e) is var inner && inner is ContractValidationException or ContractConflictException or ContractLimitException)
        {
            return inner switch
            {
                ContractConflictException => Results.Conflict(new { error = inner.Message }),
                ContractLimitException => Results.Json(new { error = inner.Message }, statusCode: StatusCodes.Status429TooManyRequests),
                _ => Results.BadRequest(new { error = inner.Message }),
            };
        }
    }

    /// <summary>What the audit log and traces name as the caller of an externally invoked operation.</summary>
    private static string PlatformCaller(ContractPlane plane) => plane switch
    {
        ContractPlane.Site => "site",
        ContractPlane.Admin => "admin",
        _ => "ai",
    };

    public static bool IsExposed(OperationDescriptor op, ContractPlane plane) => plane switch
    {
        ContractPlane.Site => op.Expose.HasFlag(OpExposure.Site),
        ContractPlane.Admin => op.Expose.HasFlag(OpExposure.Admin),
        ContractPlane.Ai => op.Expose.HasFlag(OpExposure.Ai),
        _ => false,
    };

    private static Exception Unwrap(Exception e) =>
        e is TargetInvocationException { InnerException: { } inner } ? Unwrap(inner) : e;
}
