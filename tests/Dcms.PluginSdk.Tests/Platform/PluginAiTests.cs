using System.Net;
using System.Text;
using System.Text.Json;
using Dcms.PluginSdk.Abstractions;
using Dcms.PluginSdk.Abstractions.Contracts;
using Dcms.PluginSdk.Abstractions.Platform;
using Dcms.PluginSdk.Runtime.Platform;
using Dcms.Shared.Security;
using Microsoft.Extensions.Logging.Abstractions;

namespace Dcms.PluginSdk.Tests.Platform;

public class PluginAiTests
{
    private static readonly Guid Tenant = Guid.NewGuid();

    private sealed record Ctx : IPluginContext
    {
        public Guid TenantId => Tenant;
        public string PluginId => "forms";
        public PluginInstanceContext? Instance => null;
        public PluginActor Actor => PluginActor.System;
        public IPluginContracts Contracts => throw new NotSupportedException();
    }

    private sealed class Tokens : IServiceTokenProvider
    {
        public string? Scope { get; private set; }

        public Task<string> GetTokenAsync(string scope, CancellationToken ct = default)
        {
            Scope = scope;
            return Task.FromResult("service-token");
        }
    }

    private sealed class Gateway(HttpStatusCode status, string body) : HttpMessageHandler, IHttpClientFactory
    {
        public JsonElement? Sent { get; private set; }

        public HttpClient CreateClient(string name) => new(this) { BaseAddress = new Uri("http://ai-gateway") };

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            request.Headers.Authorization!.Parameter.Should().Be("service-token");
            Sent = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct)).RootElement.Clone();
            return new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        }
    }

    [Fact]
    public async Task Completes_as_the_callers_tenant_with_a_dcms_ai_token()
    {
        var tokens = new Tokens();
        var gateway = new Gateway(HttpStatusCode.OK, """{"provider":"x","model":"y","text":"Hello"}""");
        var ai = new PluginAi(new Ctx(), tokens, gateway, NullLogger<PluginAi>.Instance);

        var result = await ai.CompleteAsync(new AiCompletionRequest("Hi", MaxTokens: 99_999), TestContext.Current.CancellationToken);

        result.Text.Should().Be("Hello");
        tokens.Scope.Should().Be("dcms.ai");
        gateway.Sent!.Value.GetProperty("tenantId").GetGuid().Should().Be(Tenant);
        gateway.Sent.Value.GetProperty("maxTokens").GetInt32().Should().Be(PluginAi.MaxTokensCeiling);
    }

    [Fact]
    public async Task A_spent_quota_is_a_limit_not_an_empty_answer()
    {
        var ai = new PluginAi(new Ctx(), new Tokens(), new Gateway(HttpStatusCode.TooManyRequests, "{}"), NullLogger<PluginAi>.Instance);

        var act = () => ai.CompleteAsync(new AiCompletionRequest("Hi"), TestContext.Current.CancellationToken);

        await act.Should().ThrowAsync<ContractLimitException>();
    }
}
