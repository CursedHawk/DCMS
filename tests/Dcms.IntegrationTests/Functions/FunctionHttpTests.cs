extern alias AdminApiApp;
extern alias ScriptRunnerApp;

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Dcms.IntegrationTests.Cms;
using Dcms.Plugins.Functions.Build;
using Dcms.Plugins.Functions.Http;
using Dcms.Plugins.Functions.Runtime;
using Dcms.Shared.Data.Functions;
using Dcms.Shared.Data.Rls;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace Dcms.IntegrationTests.Functions;

/// <summary>
/// HTTP functions (ADR 0029, F7) end to end: a call to a tenant site's
/// <c>/api/{app}/fn/{function}</c> reaches content-api, which forwards it as a service to
/// admin-api, which checks the function's declared access against the live deployment and runs
/// it in the real sandbox. API keys are made, scoped and revoked through the admin's routes.
/// </summary>
[Collection(ContentFlowCollection.Name)]
public sealed class FunctionHttpTests(ContentFlowFixture fixture, DenoBinary deno, FunctionRunnerTests.Esbuild esbuild,
    FunctionRunnerTests.TypeScript typescript)
    : IClassFixture<DenoBinary>, IClassFixture<FunctionRunnerTests.Esbuild>, IClassFixture<FunctionRunnerTests.TypeScript>, IAsyncLifetime
{
    private WebApplicationFactory<ScriptRunnerApp::Program>? _runner;

    public ValueTask InitializeAsync()
    {
        if (DockerFactAttribute.Available)
        {
            _runner = new WebApplicationFactory<ScriptRunnerApp::Program>().WithWebHostBuilder(b =>
            {
                b.UseSetting("ScriptRunner:DenoPath", deno.Path);
                b.UseSetting("ScriptRunner:EsbuildPath", esbuild.Path);
                b.UseSetting("ScriptRunner:TypeScriptPath", typescript.Path);
            });
        }
        return ValueTask.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        if (_runner is not null) await _runner.DisposeAsync();
    }

    [DockerFact]
    public async Task A_site_calls_its_functions_as_each_declares_and_api_keys_open_only_theirs()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var admin = fixture.Admin.WithWebHostBuilder(b => b.ConfigureTestServices(s =>
            s.AddSingleton<FunctionSocketFactory>(_ => token =>
                _runner!.Server.CreateWebSocketClient().ConnectAsync(new Uri(_runner.Server.BaseAddress, "session"), token))));
        await using var content = fixture.Content.WithWebHostBuilder(b => b.ConfigureTestServices(s =>
            s.AddHttpClient(SiteEndpoints.HttpClientName)
                .ConfigurePrimaryHttpMessageHandler(() => new AsService(admin))
                .ConfigureHttpClient(c => c.BaseAddress = new Uri("http://admin.test"))));
        var adminClient = admin.CreateClient();
        var site = content.CreateClient();

        var owner = Guid.NewGuid();
        var tenant = "fnh-" + Guid.NewGuid().ToString("N")[..8];
        var created = await adminClient.SendAsync(Req(HttpMethod.Post, "/api/admin/tenants", Guid.NewGuid(), null,
            new { slug = tenant, name = tenant, ownerUserId = owner, ownerEmail = $"{owner:N}@dcms.test" }, superAdmin: true), ct);
        created.StatusCode.Should().Be(HttpStatusCode.Created);
        var tenantId = (await created.Content.ReadFromJsonAsync<JsonElement>(ct)).GetProperty("tenantId").GetGuid();
        var installed = await adminClient.SendAsync(Req(HttpMethod.Post, "/api/admin/plugins/instances", owner, tenant,
            new { pluginId = "functions", slug = "fn", name = "Functions" }), ct);
        installed.StatusCode.Should().Be(HttpStatusCode.Created, await installed.Content.ReadAsStringAsync(ct));
        var instanceId = (await installed.Content.ReadFromJsonAsync<JsonElement>(ct)).GetProperty("id").GetGuid();

        await DeployAsync(admin, tenantId, instanceId, new()
        {
            ["functions/hello.ts"] = """
                import { defineFunction } from "@dcms/functions";
                export default defineFunction({
                  access: { http: ["public"] },
                  handler: (input: { name?: string }, ctx) => ({ message: `Hello, ${input.name ?? "world"}!`, method: ctx.request?.method }),
                });
                """,
            ["functions/made.ts"] = """
                import { defineFunction, respond } from "@dcms/functions";
                export default defineFunction({
                  access: { http: ["public"] },
                  handler: () => respond(201, "made", { "content-type": "text/plain", "set-cookie": "x=1", location: "/there" }),
                });
                """,
            ["functions/orders/total.ts"] = """
                import { defineFunction } from "@dcms/functions";
                interface Order { lines: number[]; coupon?: string }
                export default defineFunction({
                  access: { http: ["public"], methods: ["post"] },
                  handler: (input: Order): { total: number } => ({ total: input.lines.reduce((a, b) => a + b, 0) }),
                });
                """,
            ["functions/search.ts"] = """
                import { defineFunction } from "@dcms/functions";
                export default defineFunction({
                  access: { http: ["public"], methods: ["get", "delete"] },
                  handler: (input: { q: string; limit?: number; exact?: boolean }) => input,
                });
                """,
            ["functions/partner.ts"] = """
                import { defineFunction } from "@dcms/functions";
                export default defineFunction({ access: { http: ["apiKey"] }, handler: () => "for partners" });
                """,
            ["functions/members.ts"] = """
                import { defineFunction } from "@dcms/functions";
                export default defineFunction({ access: { http: ["user"] }, handler: () => "for members" });
                """,
            ["functions/internal.ts"] = """
                import { defineFunction } from "@dcms/functions";
                export default defineFunction({ handler: () => "not over HTTP" });
                """,
            ["functions/boom.ts"] = """
                import { defineFunction } from "@dcms/functions";
                export default defineFunction({ access: { http: ["public"] }, handler: () => { throw new Error("secret detail"); } });
                """,
        }, ct);

        Task<HttpResponseMessage> Call(HttpMethod method, string path, object? body = null, string? key = null)
        {
            var req = new HttpRequestMessage(method, path);
            req.Headers.Add("X-Dcms-Tenant", tenant);
            if (key is not null) req.Headers.Add("X-Api-Key", key);
            if (body is not null) req.Content = JsonContent.Create(body);
            return site.SendAsync(req, ct);
        }

        // Public, GET (input from the query) and POST (input from the body).
        var get = await Call(HttpMethod.Get, "/api/fn/fn/hello?name=Ada");
        get.StatusCode.Should().Be(HttpStatusCode.OK, await get.Content.ReadAsStringAsync(ct));
        (await get.Content.ReadFromJsonAsync<JsonElement>(ct)).GetProperty("message").GetString().Should().Be("Hello, Ada!");
        var post = await (await Call(HttpMethod.Post, "/api/fn/fn/hello", new { name = "Grace" })).Content.ReadFromJsonAsync<JsonElement>(ct);
        post.GetProperty("message").GetString().Should().Be("Hello, Grace!");
        post.GetProperty("method").GetString().Should().Be("POST");

        // A function in a folder answers at its path, at the methods it declared, taking what its types say.
        var total = await Call(HttpMethod.Post, "/api/fn/fn/orders/total", new { lines = new[] { 1, 2, 3 } });
        total.StatusCode.Should().Be(HttpStatusCode.OK, await total.Content.ReadAsStringAsync(ct));
        (await total.Content.ReadFromJsonAsync<JsonElement>(ct)).GetProperty("total").GetInt32().Should().Be(6);
        (await Call(HttpMethod.Get, "/api/fn/fn/orders/total")).StatusCode.Should().Be(HttpStatusCode.MethodNotAllowed);
        (await Call(HttpMethod.Get, "/api/fn/fn/orders")).StatusCode.Should().Be(HttpStatusCode.NotFound);
        var wrong = await Call(HttpMethod.Post, "/api/fn/fn/orders/total", new { lines = "three", coupon = 5 });
        wrong.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await wrong.Content.ReadFromJsonAsync<JsonElement>(ct)).GetProperty("problems").EnumerateArray().Select(p => p.GetString())
            .Should().BeEquivalentTo(["input.lines: expected array, got a string.", "input.coupon: expected string, got a number."]);
        (await Call(HttpMethod.Post, "/api/fn/fn/orders/total", new { })).StatusCode.Should().Be(HttpStatusCode.BadRequest);

        // GET takes the query string, read as the input's types: a number is a number.
        var found = await Call(HttpMethod.Get, "/api/fn/fn/search?q=acme&limit=5&exact=true");
        found.StatusCode.Should().Be(HttpStatusCode.OK, await found.Content.ReadAsStringAsync(ct));
        JsonNode.DeepEquals(JsonNode.Parse(await found.Content.ReadAsStringAsync(ct)), JsonNode.Parse("""{"q":"acme","limit":5,"exact":true}"""))
            .Should().BeTrue();
        (await Call(HttpMethod.Get, "/api/fn/fn/search?q=acme&limit=many")).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await Call(HttpMethod.Get, "/api/fn/fn/search")).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await Call(HttpMethod.Delete, "/api/fn/fn/search?q=x")).StatusCode.Should().Be(HttpStatusCode.OK);
        (await Call(HttpMethod.Put, "/api/fn/fn/search", new { q = "x" })).StatusCode.Should().Be(HttpStatusCode.MethodNotAllowed);

        // Its own answer: status and allowed headers; never a cookie.
        var made = await Call(HttpMethod.Get, "/api/fn/fn/made");
        made.StatusCode.Should().Be(HttpStatusCode.Created);
        (await made.Content.ReadAsStringAsync(ct)).Should().Be("made");
        made.Headers.Location!.ToString().Should().Be("/there");
        made.Headers.Contains("Set-Cookie").Should().BeFalse();

        // Not callable over HTTP, or not there: the same 404. A failure says no more than that.
        (await Call(HttpMethod.Get, "/api/fn/fn/internal")).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await Call(HttpMethod.Get, "/api/fn/fn/nope")).StatusCode.Should().Be(HttpStatusCode.NotFound);
        var boom = await Call(HttpMethod.Get, "/api/fn/fn/boom");
        boom.StatusCode.Should().Be(HttpStatusCode.InternalServerError);
        (await boom.Content.ReadAsStringAsync(ct)).Should().NotContain("secret detail");

        // Every HTTP run is in the history, succeeded or failed. The sweep keeps a function's newest
        // successful HTTP runs only, so a busy public endpoint cannot fill it.
        async Task<List<(string Function, string Status)>> HttpRuns()
        {
            await using var conn = new NpgsqlConnection(fixture.PostgresConnectionString);
            await conn.OpenAsync(ct);
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = """SELECT "Function", "Status" FROM functions.runs WHERE "InstanceId" = @instance AND "TriggerKind" = 'http'""";
            cmd.Parameters.AddWithValue("instance", instanceId);
            var found = new List<(string, string)>();
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct)) found.Add((reader.GetString(0), reader.GetString(1)));
            return found;
        }
        var recorded = await HttpRuns();
        recorded.Where(r => r.Function == "hello").Should().HaveCount(2).And.OnlyContain(r => r.Status == "Succeeded");
        recorded.Should().ContainSingle(r => r.Function == "made").Which.Status.Should().Be("Succeeded");
        recorded.Should().ContainSingle(r => r.Function == "boom").Which.Status.Should().Be("Failed");
        await using (var conn = new NpgsqlConnection(fixture.PostgresConnectionString))
        {
            // Older copies of a real successful run of "hello", past the number kept.
            await conn.OpenAsync(ct);
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                INSERT INTO functions.runs ("Id", "TenantId", "EnvironmentId", "InstanceId", "Function", "DeploymentId", "DeploymentNumber",
                    "TriggerKind", "TriggerEventId", "Status", "Attempts", "CorrelationId", "Depth", "Calls", "DurationMs", "CreatedAt", "NextAttemptAt", "FinishedAt")
                SELECT gen_random_uuid(), "TenantId", "EnvironmentId", "InstanceId", "Function", "DeploymentId", "DeploymentNumber",
                    "TriggerKind", gen_random_uuid(), "Status", 1, gen_random_uuid(), 1, 0, 1, "CreatedAt" - make_interval(mins => n), "NextAttemptAt", "FinishedAt"
                FROM (SELECT * FROM functions.runs WHERE "InstanceId" = @instance AND "Function" = 'hello' LIMIT 1) one, generate_series(1, @extra) n
                """;
            cmd.Parameters.AddWithValue("instance", instanceId);
            cmd.Parameters.AddWithValue("extra", RunWorker.KeptHttpRuns);
            await cmd.ExecuteNonQueryAsync(ct);
        }
        await ActivatorUtilities.CreateInstance<RunWorker>(admin.Services).CleanupAsync(ct);
        recorded = await HttpRuns();
        recorded.Count(r => r.Function == "hello").Should().Be(RunWorker.KeptHttpRuns);
        recorded.Should().Contain(r => r.Function == "boom").And.Contain(r => r.Function == "made");

        // The site's OpenAPI document (and so its generated client) has what is callable over HTTP, and only that.
        var paths = (await (await Call(HttpMethod.Get, "/api/openapi.json")).Content.ReadFromJsonAsync<JsonObject>(ct))!["paths"]!.AsObject();
        paths["/api/fn/fn/hello"]!["post"]!["x-dcms-client"]!.AsArray().Select(p => p!.GetValue<string>()).Should().Equal("fn", "hello", "post");
        paths["/api/fn/fn/partner"]!["get"]!["parameters"]![0]!["name"]!.GetValue<string>().Should().Be("X-Api-Key");
        paths.ContainsKey("/api/fn/fn/internal").Should().BeFalse();
        // Only the methods each answers, with its types: the body or query parameters in, the result out.
        var totalPath = paths["/api/fn/fn/orders/total"]!.AsObject();
        totalPath.Select(p => p.Key).Should().Equal("post");
        totalPath["post"]!["x-dcms-client"]!.AsArray().Select(p => p!.GetValue<string>()).Should().Equal("fn", "orders", "total", "post");
        totalPath["post"]!["requestBody"]!["content"]!["application/json"]!["schema"]!["required"]!.AsArray().Select(r => r!.GetValue<string>())
            .Should().Equal("lines");
        totalPath["post"]!["responses"]!["200"]!["content"]!["application/json"]!["schema"]!["properties"]!["total"]!["type"]!.GetValue<string>()
            .Should().Be("number");
        var search = paths["/api/fn/fn/search"]!.AsObject();
        search.Select(p => p.Key).Should().BeEquivalentTo(["get", "delete"]);
        search["get"]!["parameters"]!.AsArray().Select(p => (p!["name"]!.GetValue<string>(), p["in"]!.GetValue<string>(), p["required"]!.GetValue<bool>()))
            .Should().BeEquivalentTo([("q", "query", true), ("limit", "query", false), ("exact", "query", false)]);
        paths["/api/fn/fn/hello"]!.AsObject().Select(p => p.Key).Should().BeEquivalentTo(["get", "post"]);
        using (var scope = admin.Services.CreateScope())
        {
            var live = await LiveFunctions.ReadAsync(scope.ServiceProvider.GetRequiredService<FunctionsDbContext>(), tenantId, tenantId, instanceId, ct);
            live.Deployment.Should().Be(1);
            live.Functions.Select(f => f.Name).Should().BeEquivalentTo(["hello", "made", "orders/total", "search", "partner", "members", "boom"]);
        }

        // Signed-in only, called anonymously.
        (await Call(HttpMethod.Get, "/api/fn/fn/members")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);

        // API keys: none, a wrong one, a scoped one, a revoked one.
        (await Call(HttpMethod.Get, "/api/fn/fn/partner")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await Call(HttpMethod.Get, "/api/fn/fn/partner", key: "dfk_not-a-real-key-at-all")).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        var key = await (await adminClient.SendAsync(Req(HttpMethod.Post, "/api/admin/plugins/fn/_functions/keys", owner, tenant,
            new { name = "Partner" }), ct)).Content.ReadFromJsonAsync<JsonElement>(ct);
        var secret = key.GetProperty("secret").GetString()!;
        var ok = await Call(HttpMethod.Get, "/api/fn/fn/partner", key: secret);
        ok.StatusCode.Should().Be(HttpStatusCode.OK);
        (await ok.Content.ReadFromJsonAsync<string>(ct)).Should().Be("for partners");
        var listed = await (await adminClient.SendAsync(Req(HttpMethod.Get, "/api/admin/plugins/fn/_functions/keys", owner, tenant), ct))
            .Content.ReadAsStringAsync(ct);
        listed.Should().Contain("Partner").And.NotContain(secret, "a key is shown once");

        var scoped = await (await adminClient.SendAsync(Req(HttpMethod.Post, "/api/admin/plugins/fn/_functions/keys", owner, tenant,
            new { name = "Hello only", functions = new[] { "hello" } }), ct)).Content.ReadFromJsonAsync<JsonElement>(ct);
        (await Call(HttpMethod.Get, "/api/fn/fn/partner", key: scoped.GetProperty("secret").GetString())).StatusCode.Should().Be(HttpStatusCode.Forbidden);

        (await adminClient.SendAsync(Req(HttpMethod.Delete, $"/api/admin/plugins/fn/_functions/keys/{key.GetProperty("key").GetProperty("id").GetGuid()}",
            owner, tenant), ct)).StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await Call(HttpMethod.Get, "/api/fn/fn/partner", key: secret)).StatusCode.Should().Be(HttpStatusCode.Forbidden);

        // Asynchronously: a tracked job, answered at once.
        var later = await Call(HttpMethod.Post, "/api/fn/fn/hello?async=1", new { name = "later" });
        later.StatusCode.Should().Be(HttpStatusCode.Accepted);
        (await later.Content.ReadFromJsonAsync<JsonElement>(ct)).GetProperty("jobId").GetGuid().Should().NotBeEmpty();

        // A body past the cap is refused before anything runs.
        var big = new HttpRequestMessage(HttpMethod.Post, "/api/fn/fn/hello") { Content = new StringContent(new string('x', SiteEndpoints.MaxBodyBytes + 1)) };
        big.Headers.Add("X-Dcms-Tenant", tenant);
        (await site.SendAsync(big, ct)).StatusCode.Should().Be(HttpStatusCode.RequestEntityTooLarge);

        // The service route refuses anyone without its scope, however authenticated.
        var direct = new HttpRequestMessage(HttpMethod.Post, InvokeEndpoint.Route) { Content = JsonContent.Create(new { }) };
        direct.Headers.Add("X-Test-Sub", "dcms-admin-api");
        direct.Headers.Add("X-Test-Scope", "dcms.social");
        (await adminClient.SendAsync(direct, ct)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    /// <summary>Builds <paramref name="files"/> with the runner and makes the result the instance's live deployment.</summary>
    private async Task DeployAsync(WebApplicationFactory<AdminApiApp::Program> admin, Guid tenantId, Guid instanceId,
        Dictionary<string, string> files, CancellationToken ct)
    {
        var res = await _runner!.CreateClient().PostAsJsonAsync("/build", new { files, sdkTypes = FunctionTypings.Generate() }, ct);
        var built = await res.Content.ReadFromJsonAsync<JsonObject>(ct);
        built!["ok"]!.GetValue<bool>().Should().BeTrue(built.ToJsonString());
        var functions = built["functions"]!.AsArray();
        var (specs, problems) = FunctionManifest.Read(functions.ToDictionary(f => f!["name"]!.GetValue<string>(), f => f!["definition"]?.DeepClone()));
        problems.Should().BeEmpty();
        // What Builder adds from the build: each function's input and output types.
        var byName = functions.ToDictionary(f => f!["name"]!.GetValue<string>());
        specs = specs.Select(s => s with { Input = FunctionManifest.Schema(byName[s.Name]!["input"]), Output = FunctionManifest.Schema(byName[s.Name]!["output"]) }).ToList();

        using var scope = admin.Services.CreateScope();
        using var rls = RlsScope.Environment(tenantId, tenantId);
        var db = scope.ServiceProvider.GetRequiredService<FunctionsDbContext>();
        db.Deployments.Add(new FunctionDeployment
        {
            Id = Guid.CreateVersion7(),
            TenantId = tenantId,
            EnvironmentId = tenantId,
            InstanceId = instanceId,
            Number = 1,
            CommitSha = "test",
            Status = FunctionDeploymentStatus.Active,
            Manifest = FunctionManifest.Serialize(specs),
            Bundles = JsonSerializer.Serialize(functions.ToDictionary(f => f!["name"]!.GetValue<string>(), f => f!["bundle"]!.GetValue<string>())),
            VouchedPermissions = FunctionManifest.RequiredPermissions(specs),
            ActivatedAt = DateTimeOffset.UtcNow,
        });
        await db.SaveChangesAsync(ct);
    }

    private static HttpRequestMessage Req(HttpMethod method, string url, Guid user, string? tenant, object? body = null, bool superAdmin = false)
    {
        var req = new HttpRequestMessage(method, url);
        req.Headers.Add("X-Test-Sub", user.ToString());
        req.Headers.Add("X-Test-Email", $"{user:N}@dcms.test");
        if (superAdmin) req.Headers.Add("X-Test-Roles", "SuperAdmin");
        if (tenant is not null) req.Headers.Add("X-Dcms-Tenant", tenant);
        if (body is not null) req.Content = JsonContent.Create(body);
        return req;
    }

    /// <summary>content-api's call into this admin host as the client-credentials caller it is, with the scope it asked for.</summary>
    private sealed class AsService(WebApplicationFactory<AdminApiApp::Program> admin) : DelegatingHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            InnerHandler ??= admin.Server.CreateHandler();
            var scope = request.Headers.Authorization?.Parameter?.Replace("stub-", "") ?? "";
            request.Headers.Authorization = null;
            request.Headers.Add("X-Test-Sub", "dcms-admin-api");
            request.Headers.Add("X-Test-Scope", scope);
            return base.SendAsync(request, ct);
        }
    }
}
