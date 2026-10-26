using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Dcms.IntegrationTests.Cms;
using Dcms.IntegrationTests.DynamicApps;

namespace Dcms.IntegrationTests.Visitors;

/// <summary>
/// A builder preview signs visitors in to the environment it previews (ADR 0026: no sandbox), and
/// the preview proxy carries the
/// visitor's token (<c>X-Dcms-Visitor-Token</c>; Authorization is the console's) to content-api,
/// so the preview's next calls know who it is — as the site's would.
/// </summary>
[Collection(ContentFlowCollection.Name)]
public sealed class PreviewVisitorTests(ContentFlowFixture fixture)
{
    [DockerFact]
    public async Task A_preview_visitor_is_a_real_visitor_and_is_known_on_the_next_call()
    {
        var ct = TestContext.Current.CancellationToken;
        var app = await AppHarness.CreateAsync(fixture, ct);
        await app.InstallAsync("visitor-auth", "members", "{}", ct);
        var created = await app.Admin.SendAsync(AppHarness.Req(HttpMethod.Post, "/api/admin/sites", app.Owner, app.Tenant,
            body: new { name = "Club", renderMode = "StaticPrerender" }), ct);
        created.StatusCode.Should().Be(HttpStatusCode.Created, await created.Content.ReadAsStringAsync(ct));
        var site = (await created.Content.ReadFromJsonAsync<JsonElement>(ct)).GetProperty("id").GetGuid();

        HttpRequestMessage Preview(HttpMethod method, string path, string? token = null, object? body = null)
        {
            var req = AppHarness.Req(method, $"/api/admin/sites/{site}/preview/api/members{path}", app.Owner, app.Tenant, body: body);
            if (token is not null) req.Headers.Add("X-Dcms-Visitor-Token", token);
            return req;
        }

        var register = await app.Admin.SendAsync(Preview(HttpMethod.Post, "/register", body: new { email = "pre@view.test", password = "correct horse battery staple" }), ct);
        register.StatusCode.Should().Be(HttpStatusCode.OK, await register.Content.ReadAsStringAsync(ct));
        var token = (await register.Content.ReadFromJsonAsync<JsonElement>(ct)).GetProperty("accessToken").GetString()!;

        var me = await app.Admin.SendAsync(Preview(HttpMethod.Get, "/me", token), ct);
        me.StatusCode.Should().Be(HttpStatusCode.OK, await me.Content.ReadAsStringAsync(ct));
        (await me.Content.ReadFromJsonAsync<JsonElement>(ct)).GetProperty("email").GetString().Should().Be("pre@view.test");

        (await app.Admin.SendAsync(Preview(HttpMethod.Get, "/me"), ct)).StatusCode
            .Should().Be(HttpStatusCode.Unauthorized, "no token: nobody is signed in");
        (await app.Admin.SendAsync(Preview(HttpMethod.Get, "/me", token + " other"), ct)).StatusCode
            .Should().Be(HttpStatusCode.Unauthorized, "a header that is not a token is not passed on");

        // The account is the environment's own: the site knows it too.
        var content = fixture.Content.CreateClient();
        var live = new HttpRequestMessage(HttpMethod.Post, "/api/members/login") { Content = JsonContent.Create(new { email = "pre@view.test", password = "correct horse battery staple" }) };
        live.Headers.Add("X-Dcms-Tenant", app.Tenant);
        (await content.SendAsync(live, ct)).StatusCode.Should().Be(HttpStatusCode.OK, "a preview acts on the environment's live data");
    }
}
