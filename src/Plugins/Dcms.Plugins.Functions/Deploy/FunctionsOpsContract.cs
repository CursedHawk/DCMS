using System.Text.Json;
using System.Text.Json.Nodes;
using Dcms.Plugins.Functions.Api;
using Dcms.Plugins.Functions.Http;
using Dcms.PluginSdk.Abstractions.Contracts;
using Dcms.PluginSdk.Abstractions.Platform;
using Microsoft.Extensions.DependencyInjection;

namespace Dcms.Plugins.Functions.Deploy;

/// <summary><c>functions.ops@1</c>: the console's runs, keys and secrets routes as operations, through the same services.</summary>
internal sealed class FunctionsOpsContract : IFunctionsOps
{
    private readonly IServiceProvider services;
    private readonly IPluginContext context;

    public FunctionsOpsContract(IServiceProvider services, IPluginContext context)
    {
        (this.services, this.context) = (services, context);
        // As FunctionsCodeContract: what this scope resolves as "the plugin's context" must be this instance's.
        var accessor = services.GetRequiredService<Dcms.PluginSdk.Runtime.Contracts.PluginContextAccessor>();
        accessor.Current ??= context;
    }

    private DeploymentService Deployments => ActivatorUtilities.CreateInstance<DeploymentService>(services, context);

    private ApiKeyService Keys => ActivatorUtilities.CreateInstance<ApiKeyService>(services, context);

    private IPluginSecrets Secrets => context.Contracts.Get<IPluginSecrets>();

    private Guid InstanceId => context.Instance?.InstanceId ?? throw new ContractValidationException("functions.ops@1 is served by a Functions app.");

    public async Task<JsonNode> ListRunsAsync(RunQuery input, CancellationToken ct) =>
        Node(await Deployments.RunsAsync(input.Function, input.Status, Math.Max(input.Page, 1), ct));

    public async Task<JsonNode> GetRunAsync(RunRef input, CancellationToken ct) =>
        Node(await Deployments.RunAsync(input.RunId, ct) ?? throw new ContractValidationException($"There is no run {input.RunId}."));

    public async Task<JsonNode> ListKeysAsync(CancellationToken ct) => Node(await Keys.ListAsync(ct));

    public async Task RevokeKeyAsync(KeyRef input, CancellationToken ct)
    {
        if (!await Keys.RevokeAsync(input.KeyId, ct))
        {
            throw new ContractValidationException($"There is no key {input.KeyId}.");
        }
    }

    public async Task<NameList> ListSecretsAsync(CancellationToken ct) =>
        new((await Secrets.ListAsync(new SecretNames(InstanceId), ct)).Names);

    public Task SetSecretAsync(SecretWrite input, CancellationToken ct) =>
        Secrets.SetAsync(new SetSecret(input.Name, input.Value, InstanceId), ct);

    public async Task DeleteSecretAsync(SecretRef input, CancellationToken ct)
    {
        if (!(await Secrets.DeleteAsync(new SecretName(input.Name, InstanceId), ct)).Found)
        {
            throw new ContractValidationException($"There is no secret {input.Name}.");
        }
    }

    private static JsonNode Node<T>(T value) => JsonSerializer.SerializeToNode(value, JsonSerializerOptions.Web)!;
}
