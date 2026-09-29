using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Schema;
using Dcms.PluginSdk.Abstractions.Contracts;

namespace Dcms.PluginSdk.Runtime.Contracts;

/// <summary>
/// Reflects a <c>[DcmsContract]</c> interface into its <see cref="ContractDescriptor"/>, and
/// refuses at startup any shape the other planes could not serve: a method without
/// <c>[Operation]</c> (and so without a stated risk), overloads, properties, or a signature that
/// is not one input record plus a CancellationToken returning a Task.
/// </summary>
public static class ContractDescriptorBuilder
{
    /// <summary>The wire format every plane speaks: camelCase web defaults.</summary>
    public static readonly JsonSerializerOptions Json = JsonSerializerOptions.Web;

    private static readonly JsonSchemaExporterOptions SchemaOptions = new() { TreatNullObliviousAsNonNullable = true };

    public static ContractDescriptor Build(Type contract, string? providerPluginId)
    {
        if (!contract.IsInterface)
        {
            throw Invalid(contract, "must be an interface");
        }
        var attribute = contract.GetCustomAttribute<DcmsContractAttribute>()
            ?? throw Invalid(contract, "is not marked [DcmsContract]");
        var id = attribute.Id;
        if (!ContractIds.IsValid(id))
        {
            throw Invalid(contract, $"has invalid id '{id}' (expected dotted kebab-case name '@' major >= 1)");
        }
        if (ContractIds.IsPlatform(id) != (providerPluginId is null))
        {
            throw Invalid(contract, providerPluginId is null
                ? $"is registered as a platform contract but '{id}' is not in the dcms.* namespace"
                : $"'{id}' is reserved for the platform; plugin '{providerPluginId}' cannot provide it");
        }
        if (contract.GetProperties().Length > 0 || contract.GetEvents().Length > 0)
        {
            throw Invalid(contract, "may declare methods only");
        }

        var operations = new List<OperationDescriptor>();
        foreach (var method in contract.GetMethods())
        {
            operations.Add(BuildOperation(contract, method));
        }
        var duplicate = operations.GroupBy(o => o.Name, StringComparer.Ordinal).FirstOrDefault(g => g.Count() > 1);
        if (duplicate is not null)
        {
            throw Invalid(contract, $"declares operation '{duplicate.Key}' more than once (overloads are not allowed)");
        }

        var events = attribute.Events.Select(t => BuildEvent(contract, t)).ToList();

        return new ContractDescriptor(id, attribute.Name, attribute.Major, attribute.Description, providerPluginId, operations, events)
        {
            ContractType = contract,
        };
    }

    private static OperationDescriptor BuildOperation(Type contract, MethodInfo method)
    {
        var op = method.GetCustomAttribute<OperationAttribute>()
            ?? throw Invalid(contract, $"method '{method.Name}' has no [Operation(risk)] — every operation must state its risk");

        if (method.IsGenericMethod)
        {
            throw Invalid(contract, $"method '{method.Name}' must not be generic");
        }

        var parameters = method.GetParameters();
        if (parameters.Length == 0 || parameters[^1].ParameterType != typeof(CancellationToken) || parameters.Length > 2)
        {
            throw Invalid(contract, $"method '{method.Name}' must be (TInput input, CancellationToken ct) or (CancellationToken ct)");
        }
        var inputType = parameters.Length == 2 ? parameters[0].ParameterType : null;
        if (inputType is not null && (inputType.IsPrimitive || inputType == typeof(string)))
        {
            throw Invalid(contract, $"method '{method.Name}' must take one input record, not a bare {inputType.Name}");
        }

        Type? outputType;
        if (method.ReturnType == typeof(Task))
        {
            outputType = null;
        }
        else if (method.ReturnType.IsGenericType && method.ReturnType.GetGenericTypeDefinition() == typeof(Task<>))
        {
            outputType = method.ReturnType.GetGenericArguments()[0];
        }
        else
        {
            throw Invalid(contract, $"method '{method.Name}' must return Task or Task<T>");
        }

        var name = method.Name.EndsWith("Async", StringComparison.Ordinal) ? method.Name[..^5] : method.Name;
        var input = inputType is null
            ? new JsonObject { ["type"] = "object", ["properties"] = new JsonObject() }
            : Json.GetJsonSchemaAsNode(inputType, SchemaOptions);
        var output = outputType is null ? null : Json.GetJsonSchemaAsNode(outputType, SchemaOptions);
        // The exporter sees the type, not the method: Task<Profile?> and Task<Profile> both reach
        // it as Profile. Read the return's nullability here so "may be null" reaches the schema,
        // and through it the generated client's types.
        if (output is JsonObject outputObject && outputType is { IsValueType: false } && ReturnsNullable(method)
            && outputObject["type"] is JsonValue single)
        {
            outputObject["type"] = new JsonArray(single.GetValue<string>(), "null");
        }

        return new OperationDescriptor(name, op.Risk, op.Permission, op.Expose, op.ReturnsExternalText, op.Description, input, output)
        {
            Method = method,
            InputType = inputType,
            OutputType = outputType,
        };
    }

    private static bool ReturnsNullable(MethodInfo method)
    {
        // A fresh context per call: NullabilityInfoContext caches in a plain Dictionary and is
        // not safe to share between threads (registries are built concurrently in tests).
        var info = new NullabilityInfoContext().Create(method.ReturnParameter);
        return info.GenericTypeArguments is [{ ReadState: NullabilityState.Nullable }];
    }

    private static EventDescriptor BuildEvent(Type contract, Type eventType)
    {
        if (!typeof(Abstractions.IPluginEvent).IsAssignableFrom(eventType))
        {
            throw Invalid(contract, $"event '{eventType.Name}' must implement IPluginEvent");
        }
        var name = eventType.GetCustomAttribute<ContractEventAttribute>()?.Name
            ?? throw Invalid(contract, $"event '{eventType.Name}' is not marked [ContractEvent]");
        // The name becomes NATS subject tokens (plugins.events.{publisher}.{name}), so it must be
        // the same dotted kebab-case as a contract name: no wildcards, spaces or empty tokens.
        if (!ContractIds.IsValid($"{name}@1"))
        {
            throw Invalid(contract, $"event name '{name}' must be dotted kebab-case");
        }
        return new EventDescriptor(name, Json.GetJsonSchemaAsNode(eventType, SchemaOptions)) { EventType = eventType };
    }

    private static InvalidOperationException Invalid(Type contract, string problem) =>
        new($"Contract '{contract.FullName}' {problem}.");
}
