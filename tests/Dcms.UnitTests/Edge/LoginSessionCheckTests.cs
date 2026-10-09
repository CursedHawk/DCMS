using System.Net;
using System.Text;
using Dcms.Edge.Auth;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Dcms.UnitTests.Edge;

/// <summary>
/// The edge asking identity whether the login behind one of its cookies has ended — which is
/// what makes "Sign out" reach Grafana and Forgejo instead of leaving both signed in for the
/// eight hours their edge cookie lives.
/// </summary>
public class LoginSessionCheckTests
{
    private const string LoginId = "11111111-1111-1111-1111-111111111111.abc";

    [Fact]
    public async Task Asks_identity_as_the_edge_client_and_reports_an_ended_login()
    {
        var stub = new Stub(HttpStatusCode.OK, """{"ended":true}""");
        var check = Build(stub);

        (await check.HasEndedAsync(LoginId, TestContext.Current.CancellationToken)).Should().BeTrue();

        var request = stub.Requests.Single();
        request.RequestUri!.ToString().Should()
            .Be($"http://identity:8080/edge/login-sessions/{Uri.EscapeDataString(LoginId)}");
        request.Headers.Authorization!.Scheme.Should().Be("Basic");
        Encoding.UTF8.GetString(Convert.FromBase64String(request.Headers.Authorization.Parameter!))
            .Should().Be("dcms-edge:edge-secret");
    }

    [Fact]
    public async Task One_question_per_login_per_minute()
    {
        // A dashboard is a hundred panel queries in a second; each one must not be a call.
        var stub = new Stub(HttpStatusCode.OK, """{"ended":false}""");
        var check = Build(stub);
        var ct = TestContext.Current.CancellationToken;

        for (var i = 0; i < 20; i++)
        {
            (await check.HasEndedAsync(LoginId, ct)).Should().BeFalse();
        }
        stub.Requests.Should().ContainSingle();
    }

    [Theory]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.Unauthorized)]
    public async Task Keeps_the_session_when_identity_cannot_answer(HttpStatusCode status)
    {
        // Fail open: the edge is how an operator reaches Grafana to find out why identity is
        // down. Refusing every session then would remove the tool along with the service.
        var check = Build(new Stub(status, "nope"));
        (await check.HasEndedAsync(LoginId, TestContext.Current.CancellationToken)).Should().BeFalse();
    }

    private static LoginSessionCheck Build(Stub stub) => new(
        new HttpClient(stub),
        new MemoryCache(new MemoryCacheOptions()),
        Options.Create(new EdgeAuthOptions
        {
            Authority = "https://auth.highgeek.eu",
            InternalAuthority = "http://identity:8080",
            ClientSecret = "edge-secret",
        }),
        NullLogger<LoginSessionCheck>.Instance);

    private sealed class Stub(HttpStatusCode status, string body) : HttpMessageHandler
    {
        public List<HttpRequestMessage> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requests.Add(request);
            return Task.FromResult(new HttpResponseMessage(status)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            });
        }
    }
}
