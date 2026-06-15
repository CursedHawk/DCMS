using System.Text.Json;
using Json.Schema;

namespace Dcms.AdminApi.Plugins;

/// <summary>Validates a plugin instance's config JSON against the manifest's JSON Schema.</summary>
public sealed class PluginConfigValidator
{
    public (bool Valid, IReadOnlyList<string> Errors) Validate(string configJsonSchema, string configJson)
    {
        JsonSchema schema;
        JsonDocument config;
        try
        {
            schema = JsonSchema.FromText(string.IsNullOrWhiteSpace(configJsonSchema) ? "{}" : configJsonSchema);
            config = JsonDocument.Parse(string.IsNullOrWhiteSpace(configJson) ? "{}" : configJson);
        }
        catch (JsonException ex)
        {
            return (false, [$"Invalid JSON: {ex.Message}"]);
        }

        using (config)
        {
            var result = schema.Evaluate(config.RootElement, new EvaluationOptions { OutputFormat = OutputFormat.List });
            if (result.IsValid)
            {
                return (true, []);
            }

            var errors = Flatten(result)
                .Where(r => r.Errors is { Count: > 0 })
                .SelectMany(r => r.Errors!.Select(kvp => $"{r.InstanceLocation}: {kvp.Value}"))
                .DefaultIfEmpty("Configuration does not match the plugin schema.")
                .ToList();
            return (false, errors);
        }
    }

    private static IEnumerable<EvaluationResults> Flatten(EvaluationResults results)
    {
        yield return results;
        foreach (var child in (results.Details ?? []).SelectMany(Flatten))
        {
            yield return child;
        }
    }
}
