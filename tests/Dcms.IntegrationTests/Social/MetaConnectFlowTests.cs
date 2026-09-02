using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Dcms.IntegrationTests.Tenancy;

namespace Dcms.IntegrationTests.Social;

/// <summary>
/// The connect flow end to end, through the real endpoints.
///
/// <para>The callback is the only unauthenticated write path on the admin plane — Meta
/// redirects a browser to it with no bearer token and no tenant header — so the single-use
/// state row is the whole of the CSRF defence and the only thing that says which tenant a code
/// belongs to. These tests are mostly about that row, not about the happy path.</para>
/// </summary>
[Collection(AdminApiCollection.Name)]
public class MetaConnectFlowTests(AdminApiFixture fixture)
{
    [DockerFact]
    public async Task Connecting_stores_every_page_and_linked_account_without_ever_returning_a_token()
    {
        var ct = TestContext.Current.CancellationToken;
        var client = Client();
        var (owner, slug) = await ProvisionTenantAsync(client, ct);

        fixture.MetaStub.Pages.Clear();
        fixture.MetaStub.Pages.Add(MetaStubServer.Page("page-1", "Acme Bakery", "PAGE-1-TOKEN", "ig-1", "acmebakery"));

        var state = await StartConsentAsync(client, owner, slug, "facebook", ct);
        await CompleteCallbackAsync(client, state, ct);

        var connections = await ListConnectionsAsync(client, owner, slug, ct);

        connections.Should().HaveCount(2, "the Page and its linked Instagram account are separate feeds");
        var ig = connections.Single(c => c.GetProperty("accountUsername").GetString() == "acmebakery");
        ig.GetProperty("status").GetString().Should().Be("Active");
        ig.GetProperty("supportsStories").GetBoolean().Should().BeTrue("the Facebook path is the one that can read stories");

        // No ciphertext, no token, no secret — in any field, under any name. Asserted over the
        // raw JSON rather than field by field so a new property cannot quietly start leaking one.
        var raw = connections.Select(c => c.GetRawText()).Aggregate(string.Concat);
        raw.Should().NotContain("LONG-LIVED-USER-TOKEN").And.NotContain("PAGE-1-TOKEN");
        raw.Should().NotContain("Ciphertext", "the API shape must not expose the encrypted column either");
    }

    [DockerFact]
    public async Task A_state_token_cannot_be_replayed()
    {
        var ct = TestContext.Current.CancellationToken;
        var client = Client();
        var (owner, slug) = await ProvisionTenantAsync(client, ct);

        fixture.MetaStub.Pages.Clear();
        fixture.MetaStub.Pages.Add(MetaStubServer.Page("page-replay", "Replay Co", "TOKEN", "ig-replay", "replayco"));

        var state = await StartConsentAsync(client, owner, slug, "facebook", ct);
        await CompleteCallbackAsync(client, state, ct);

        // Same state, second time. This is the request an attacker who captured a redirect URL
        // out of a browser history or a referer header would make.
        var replay = await client.GetAsync($"/api/admin/social/callback?code=another-code&state={state}", ct);

        replay.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var body = await replay.Content.ReadAsStringAsync(ct);
        body.Should().NotContain("expired").And.NotContain("already",
            "the rejection must not tell a prober which of unknown / used / expired their guess was");
    }

    [DockerFact]
    public async Task An_unknown_state_token_is_rejected_and_creates_nothing()
    {
        var ct = TestContext.Current.CancellationToken;
        var client = Client();
        var (owner, slug) = await ProvisionTenantAsync(client, ct);

        var forged = await client.GetAsync(
            "/api/admin/social/callback?code=stolen-code&state=totally-made-up", ct);

        forged.StatusCode.Should().Be(
            HttpStatusCode.BadRequest, "body was: {0}", await forged.Content.ReadAsStringAsync(ct));
        (await ListConnectionsAsync(client, owner, slug, ct)).Should().BeEmpty();
    }

    [DockerFact]
    public async Task Disconnecting_destroys_the_stored_tokens()
    {
        var ct = TestContext.Current.CancellationToken;
        var client = Client();
        var (owner, slug) = await ProvisionTenantAsync(client, ct);

        fixture.MetaStub.Pages.Clear();
        fixture.MetaStub.Pages.Add(MetaStubServer.Page("page-gone", "Gone Ltd", "TOKEN"));

        var state = await StartConsentAsync(client, owner, slug, "facebook", ct);
        await CompleteCallbackAsync(client, state, ct);

        var id = (await ListConnectionsAsync(client, owner, slug, ct)).Single().GetProperty("id").GetGuid();

        var res = await client.SendAsync(
            Req(HttpMethod.Post, $"/api/admin/social/connections/{id}/disconnect", owner, slug), ct);
        res.StatusCode.Should().Be(HttpStatusCode.NoContent);

        var after = (await ListConnectionsAsync(client, owner, slug, ct)).Single();
        // Revoked rather than deleted, so sync state can still explain why a feed stopped —
        // but the credential itself has to be gone, which is the part worth asserting.
        after.GetProperty("status").GetString().Should().Be("Revoked");
        after.GetProperty("tokenExpiresAt").ValueKind.Should().Be(JsonValueKind.Null);
    }

    [DockerFact]
    public async Task Reconnecting_the_same_account_updates_the_row_rather_than_adding_one()
    {
        var ct = TestContext.Current.CancellationToken;
        var client = Client();
        var (owner, slug) = await ProvisionTenantAsync(client, ct);

        fixture.MetaStub.Pages.Clear();
        fixture.MetaStub.Pages.Add(MetaStubServer.Page("page-dup", "Dup Co", "TOKEN-1"));

        await CompleteCallbackAsync(client, await StartConsentAsync(client, owner, slug, "facebook", ct), ct);
        await CompleteCallbackAsync(client, await StartConsentAsync(client, owner, slug, "facebook", ct), ct);

        // Accumulating a row per consent would leave stale tokens behind and make "which
        // credential is live?" ambiguous for the sync worker.
        (await ListConnectionsAsync(client, owner, slug, ct)).Should().HaveCount(1);
    }

    // ---------- helpers ----------

    private HttpClient Client() => fixture.Factory.CreateClient(
        new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

    private static async Task<(Guid Owner, string Slug)> ProvisionTenantAsync(HttpClient client, CancellationToken ct)
    {
        var owner = Guid.NewGuid();
        var slug = "social-" + Guid.NewGuid().ToString("N")[..8];
        var res = await client.SendAsync(Req(HttpMethod.Post, "/api/admin/tenants", owner, null,
            body: new { slug, name = slug, ownerUserId = owner, ownerEmail = "o@dcms.test" },
            asSuperAdmin: true), ct);
        res.EnsureSuccessStatusCode();
        return (owner, slug);
    }

    private static async Task<string> StartConsentAsync(
        HttpClient client, Guid owner, string slug, string provider, CancellationToken ct)
    {
        var res = await client.SendAsync(
            Req(HttpMethod.Get, $"/api/admin/social/{provider}/connect", owner, slug), ct);
        res.StatusCode.Should().Be(HttpStatusCode.OK);

        var url = (await res.Content.ReadFromJsonAsync<JsonElement>(ct)).GetProperty("authorizeUrl").GetString()!;
        return System.Web.HttpUtility.ParseQueryString(new Uri(url).Query)["state"]!;
    }

    private static async Task CompleteCallbackAsync(HttpClient client, string state, CancellationToken ct)
    {
        // No auth headers and no tenant header: exactly what Meta's redirect looks like.
        var res = await client.GetAsync($"/api/admin/social/callback?code=auth-code&state={state}", ct);
        res.StatusCode.Should().Be(HttpStatusCode.Redirect);
        res.Headers.Location!.OriginalString.Should().Contain("social=connected");
    }

    private static async Task<List<JsonElement>> ListConnectionsAsync(
        HttpClient client, Guid owner, string slug, CancellationToken ct)
    {
        var res = await client.SendAsync(Req(HttpMethod.Get, "/api/admin/social/connections", owner, slug), ct);
        res.StatusCode.Should().Be(HttpStatusCode.OK);
        return (await res.Content.ReadFromJsonAsync<JsonElement>(ct)).EnumerateArray().ToList();
    }

    private static HttpRequestMessage Req(
        HttpMethod method, string url, Guid sub, string? slug, object? body = null, bool asSuperAdmin = false)
    {
        var req = new HttpRequestMessage(method, url);
        req.Headers.Add("X-Test-Sub", sub.ToString());
        req.Headers.Add("X-Test-Email", $"{sub:N}@dcms.test");
        if (asSuperAdmin) req.Headers.Add("X-Test-Roles", "SuperAdmin");
        if (slug is not null) req.Headers.Add("X-Dcms-Tenant", slug);
        if (body is not null) req.Content = JsonContent.Create(body);
        return req;
    }
}
