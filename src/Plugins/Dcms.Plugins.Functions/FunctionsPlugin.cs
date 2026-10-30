using Dcms.Plugins.Functions.Api;
using Dcms.PluginSdk.Abstractions;
using Dcms.PluginSdk.Abstractions.Contracts;
using Dcms.PluginSdk.Abstractions.Platform;
using Dcms.Plugins.Functions.Build;
using Dcms.Plugins.Functions.Deploy;
using System.Net.WebSockets;
using Dcms.Plugins.DynamicApps.Api;
using Dcms.Plugins.Forms.Api;
using Dcms.Plugins.UserAuth.Api;
using Dcms.Plugins.VisitorAuth.Api;
using Dcms.Plugins.Functions.Http;
using Dcms.Plugins.Functions.Runtime;
using Dcms.Shared.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

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
        consumes:
        [
            // The instance's code: one repository per environment, edited in the web IDE.
            ContractRequirement.Of<IPluginRepos>(),
            // Builds run as tracked jobs, on the workspace's jobs page.
            ContractRequirement.Of<IPluginTrackedJobs>(),
            // What functions reach, each call checked against what they declared (ADR 0029).
            ContractRequirement.Of<IDynamicAppsAutomation>(optional: true),
            ContractRequirement.Of<IPluginSecrets>(),
            // Triggers: other plugins' events (on.event), and what form submissions carried.
            ContractRequirement.Of<IFormSubmissions>(optional: true),
            ContractRequirement.Of<IVisitorProfiles>(optional: true),
            ContractRequirement.Of<IUserDirectory>(optional: true),
            // HTTP: who the signed-in site user is, whether a role grants them a function, and the throttle.
            ContractRequirement.Of<IUserIdentity>(optional: true),
            ContractRequirement.Of<IUserAccess>(optional: true),
            ContractRequirement.Of<IPluginCache>(),
        ],
        provides:
        [
            // Every app's committed row and relation events reach the functions triggered by them.
            ContractProvision.Of<IAppEventSink, AppEventSink>(),
            // Functions whose access names "role", for the roles screen to grant.
            ContractProvision.Of<IUserResources, FunctionUserResources>(),
            // The app's code for the console's assistant: write, check, test, ship, deploy.
            ContractProvision.Of<IFunctionsCode, FunctionsCodeContract>(),
        ],
        jobs: [JobDeclaration.Of<BuildJob>(BuildJob.Name), JobDeclaration.Of<RunJob>(RunJob.Name)],
        subscribes:
        [
            // A push to release builds a deployment.
            EventSubscription.Of<RepoPushed, RepoPushedHandler>(),
            EventSubscription.Of<VisitorRegistered, PlatformEventRuns>(),
            EventSubscription.Of<FormSubmitted, PlatformEventRuns>(),
            EventSubscription.Of<UserInvited, PlatformEventRuns>(),
            EventSubscription.Of<UserActivated, PlatformEventRuns>(),
        ],
        adminScreens:
        [
            AdminScreen.Settings,
            // The app's work: writing functions, shipping them, watching them run.
            new AdminScreen("code", "Code", AdminScreenScope.Instance, Permission: "edit", IconName: "Code",
                Titles: new Dictionary<string, string> { ["cs"] = "Kód" },
                Description: "Write and test the functions in the web IDE, with the generated SDK."),
            new AdminScreen("deployments", "Deployments", AdminScreenScope.Instance, Permission: "read", IconName: "Rocket",
                Titles: new Dictionary<string, string> { ["cs"] = "Nasazení" },
                Description: "Builds of the release branch: deploy one, or roll back."),
            new AdminScreen("runs", "Runs", AdminScreenScope.Instance, Permission: "logs", IconName: "Activity",
                Titles: new Dictionary<string, string> { ["cs"] = "Běhy" },
                Description: "Every run with its trigger, input, result and log."),
            // Configuring who and what may call in.
            new AdminScreen("keys", "API keys", AdminScreenScope.Instance, Permission: "keys", IconName: "KeyRound",
                Titles: new Dictionary<string, string> { ["cs"] = "API klíče" },
                Description: "Keys outside callers use to invoke HTTP functions.")
            {
                Section = AdminScreenSection.Admin,
            },
            new AdminScreen("secrets", "Secrets", AdminScreenScope.Instance, Permission: "secrets", IconName: "LockKeyhole",
                Titles: new Dictionary<string, string> { ["cs"] = "Tajné hodnoty" },
                Description: "Values functions read with secrets.get: set or replaced, never shown again.")
            {
                Section = AdminScreenSection.Admin,
            },
        ],
        category: "Integrations",
        summary: "TypeScript functions on HTTP, data changes, messages and schedules.",
        iconName: "SquareFunction",
        tags: ["functions", "serverless", "automation", "api", "typescript"]);

    public void ConfigureServices(IServiceCollection services, PluginHost host)
    {
        if (!host.IsAdmin)
        {
            // The site plane forwards HTTP calls to admin-api, where the broker runs.
            services.AddHttpClient(SiteEndpoints.HttpClientName, client =>
            {
                client.BaseAddress = new Uri(host.Configuration["Services:AdminApi"] ?? "http://localhost:5002");
                // An HTTP run is at most 30 s; the rest is the hop.
                client.Timeout = TimeSpan.FromSeconds(40);
            });
            return;
        }
        services.AddScoped<ApiKeyService>();
        services.AddScoped<DeploymentService>();
        // Builds: the files go to script-runner, which type-checks and bundles them (ADR 0029).
        // Reachable only from the brokers' own network; a cold type check takes seconds.
        services.AddHttpClient(BuildJob.HttpClientName, client =>
        {
            client.BaseAddress = new Uri(host.Configuration["Services:ScriptRunner"] ?? "http://localhost:5095");
            client.Timeout = TimeSpan.FromMinutes(3);
        });

        // Runs: a WebSocket session on the same runner, and the broker answering its calls here.
        services.AddSingleton<FunctionSocketFactory>(_ =>
        {
            var runner = new Uri(host.Configuration["Services:ScriptRunner"] ?? "http://localhost:5095");
            var session = new UriBuilder(runner) { Scheme = runner.Scheme == "https" ? "wss" : "ws", Path = "/session" }.Uri;
            return async ct =>
            {
                var socket = new ClientWebSocket();
                await socket.ConnectAsync(session, ct);
                return socket;
            };
        });
        services.AddScoped<FunctionRunner>();
        services.AddScoped<SdkTypings>();
        services.AddScoped<Builder>();
        services.AddScoped<RunQueue>();
        if (host.RunsBackgroundWork)
        {
            // Queued runs and schedules: jobs-worker's work.
            services.AddHostedService<RunWorker>();
        }
        // http.fetch: public addresses only, checked at connect time (redirects and DNS
        // rebinding included), and no redirects followed: the function sees the 3xx and decides.
        services.AddHttpClient(HostCalls.EgressClientName)
            .ConfigurePrimaryHttpMessageHandler(() =>
            {
                var handler = PublicEgress.Handler(allowPrivate: host.Configuration.GetValue("Functions:EgressAllowLocal", false));
                handler.AllowAutoRedirect = false;
                return handler;
            });
    }

    /// <summary>Site plane: <c>/api/{slug}/fn/{function}</c>.</summary>
    public void MapEndpoints(IPluginEndpointBuilder endpoints) => SiteEndpoints.Map(endpoints);

    /// <summary>Admin plane, the one route content-api forwards HTTP calls to.</summary>
    public void MapHostEndpoints(Microsoft.AspNetCore.Routing.IEndpointRouteBuilder app, PluginHost host)
    {
        if (host.IsAdmin)
        {
            InvokeEndpoint.Map(app);
        }
    }

    /// <summary>Admin plane, <c>/api/admin/plugins/{slug}/_functions/…</c>.</summary>
    public void MapAdminEndpoints(IPluginEndpointBuilder endpoints) => Endpoints.AdminEndpoints.Map(endpoints);
}
