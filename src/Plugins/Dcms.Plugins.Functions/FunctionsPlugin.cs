using Dcms.Plugins.Functions.Api;
using Dcms.PluginSdk.Abstractions;

namespace Dcms.Plugins.Functions;

/// <summary>
/// Tenant-written functions (ADR 0029): one instance owns one git repository holding many
/// functions, each run in the script-runner sandbox on HTTP calls, Dynamic Apps row events,
/// messages, schedules and jobs, and reaching DCMS only through a generated, typed SDK whose
/// every call the host checks.
///
/// <para>The instance config holds nothing a function needs: code lives in the repository,
/// what runs is a deployment row in the <c>functions</c> schema, secrets are per environment.</para>
/// </summary>
public sealed class FunctionsPlugin : IPlugin
{
    public const string PluginId = FunctionsPermissions.PluginId;

    private const string ConfigSchema = """
        {
          "type": "object",
          "properties": {
            "displayName": {
              "type": "string",
              "title": "Name",
              "description": "Shown in the admin and in the generated API description.",
              "maxLength": 120
            }
          },
          "additionalProperties": false
        }
        """;

    public PluginManifest Manifest { get; } = PluginManifest.Create(
        id: PluginId,
        name: "Functions",
        description: "Write TypeScript functions in the browser, with git history and an AI agent, and run them "
            + "on HTTP calls, data changes, messages, schedules and jobs. A typed SDK, generated from this "
            + "workspace, reaches your Dynamic Apps tables and other apps.",
        allowMultipleInstances: true,
        configJsonSchema: ConfigSchema,
        permissions:
        [
            new PermissionDefinition("read", "View functions",
                "See the functions' code, deployments and triggers."),
            new PermissionDefinition("edit", "Edit functions",
                "Change the code and test it. Nothing runs for real until it is deployed."),
            new PermissionDefinition("deploy", "Deploy functions",
                "Make a commit live, or roll back. The deployer must hold every permission the functions use."),
            new PermissionDefinition("logs", "View function runs",
                "See each run's input, output and log, which can contain your data."),
            new PermissionDefinition("keys", "Manage API keys",
                "Create and revoke the keys outside callers use to invoke functions."),
            new PermissionDefinition("secrets", "Manage secrets",
                "Set the secret values functions read. A value can be replaced, never read back."),
        ],
        adminScreens:
        [
            AdminScreen.Settings,
        ],
        category: "Integrations",
        summary: "TypeScript functions on HTTP, data changes, messages and schedules.",
        iconName: "SquareFunction",
        tags: ["functions", "serverless", "automation", "api", "typescript"]);
}
