using System.Net;
using Dcms.Shared.Security;
using Microsoft.Extensions.DependencyInjection;

namespace Dcms.UnitTests.Security;

/// <summary>
/// The token cache has to outlive a request. Registered as a typed HttpClient it was transient,
/// so every proxied console call and job tick minted a token: 530 "auth.token.issued" records in
/// three days on dev, from four services.
/// </summary>
public sealed class ServiceTokenClientTests
{
    [Fact]
    public async Task One_token_serves_every_scope_of_the_process_until_it_nears_expiry()
    {
        var handler = new CountingTokenHandler();
        var services = new ServiceCollection();
        services.Configure<ServiceClientOptions>(o => o.ClientId = "dcms-console");
        services.AddServiceTokenProvider();
        services.AddHttpClient(ServiceTokenClient.HttpClientName).ConfigurePrimaryHttpMessageHandler(() => handler);
        await using var provider = services.BuildServiceProvider();

        for (var request = 0; request < 3; request++)
        {
            await using var scope = provider.CreateAsyncScope();
            var tokens = scope.ServiceProvider.GetRequiredService<IServiceTokenProvider>();
            (await tokens.GetTokenAsync("dcms.console", TestContext.Current.CancellationToken)).Should().Be("t1");
        }

        handler.Calls.Should().Be(1);
    }

    private sealed class CountingTokenHandler : HttpMessageHandler
    {
        public int Calls;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var n = Interlocked.Increment(ref Calls);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent($$"""{"access_token":"t{{n}}","expires_in":3600}""",
                    System.Text.Encoding.UTF8, "application/json"),
            });
        }
    }
}
