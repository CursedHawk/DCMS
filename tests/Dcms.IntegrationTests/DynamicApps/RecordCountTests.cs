using System.Net;
using System.Text.Json;
using Dcms.IntegrationTests.Cms;

namespace Dcms.IntegrationTests.DynamicApps;

/// <summary>
/// A list's total is cached per filter until the next write in the app (RecordCounts). Full pages are
/// what reach the cache -- a short page knows its total without counting -- so every read here asks
/// for fewer rows than exist.
/// </summary>
[Collection(ContentFlowCollection.Name)]
public sealed class RecordCountTests(ContentFlowFixture fixture)
{
    private const string Model = """
        [
          { "op": "create", "type": "table", "value": { "apiName": "companies", "displayName": "Company", "primaryFieldId": "name",
              "fields": [ { "apiName": "name", "displayName": "Name", "required": true } ] } }
        ]
        """;

    [DockerFact]
    public async Task A_cached_total_moves_with_every_write()
    {
        var ct = TestContext.Current.CancellationToken;
        var app = await AppHarness.CreateAsync(fixture, ct);
        await app.PublishAsync(Model, ct);
        for (var i = 0; i < 3; i++)
        {
            await app.JsonAsync(HttpMethod.Post, "/_records/companies", ct, new { name = $"Company {i}" }, HttpStatusCode.Created);
        }
        async Task<int> Total(string query = "") =>
            (await app.JsonAsync(HttpMethod.Get, $"/_records/companies?pageSize=2{query}", ct)).GetProperty("total").GetInt32();

        (await Total()).Should().Be(3);
        (await Total()).Should().Be(3, "the same answer from the cache");
        (await Total("&search=Company%201")).Should().Be(1, "a filter is its own entry (and a short page)");

        var created = await app.JsonAsync(HttpMethod.Post, "/_records/companies", ct, new { name = "Company 3" }, HttpStatusCode.Created);
        (await Total()).Should().Be(4, "a create makes the next count exact");

        (await app.SendAsync(HttpMethod.Delete, $"/_records/companies/{created.GetProperty("id").GetGuid()}", null, ct))
            .StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await Total()).Should().Be(3, "and so does a delete");

        // Past the end: an empty page that is not the first still reports where the end is.
        (await app.JsonAsync(HttpMethod.Get, "/_records/companies?pageSize=2&page=5", ct)).GetProperty("total").GetInt32().Should().Be(3);
    }
}
