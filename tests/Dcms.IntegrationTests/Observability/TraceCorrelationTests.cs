using System.Net;
using System.Net.Http.Json;
using Dcms.IntegrationTests.Tenancy;
using Dcms.Shared.Audit.Http;
using Npgsql;

namespace Dcms.IntegrationTests.Observability;

/// <summary>
/// The support workflow, end to end: a user quotes the id from a response, and that id finds
/// the audit rows for what they did.
///
/// <para>Every piece of this existed separately and none of it was connected. The audit table
/// has stored <c>TraceId</c> since ADR 0007; spans were being produced; and the value was
/// never put on a response, so nobody outside the platform could name a trace. These tests
/// assert the join actually holds, because a mismatch here is invisible — both sides look
/// populated and simply refer to different things.</para>
/// </summary>
[Collection(AdminApiCollection.Name)]
public class TraceCorrelationTests(AdminApiFixture fixture)
{
    private static readonly Guid SuperAdmin = Guid.NewGuid();

    [DockerFact]
    public async Task Response_trace_header_finds_the_audit_rows_for_that_request()
    {
        var ct = TestContext.Current.CancellationToken;
        var client = fixture.Factory.CreateClient();

        var slug = "trace-" + Guid.NewGuid().ToString("N")[..8];
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/admin/tenants")
        {
            Headers =
            {
                { "X-Test-Sub", SuperAdmin.ToString() },
                { "X-Test-Roles", "SuperAdmin" },
            },
            Content = JsonContent.Create(new
            {
                slug,
                name = slug,
                ownerUserId = Guid.NewGuid(),
                ownerEmail = $"{slug}@dcms.test",
            }),
        };

        var response = await client.SendAsync(request, ct);
        response.StatusCode.Should().Be(HttpStatusCode.Created);

        response.Headers.TryGetValues(AuditMiddleware.TraceHeader, out var traceValues).Should().BeTrue(
            "every response carries the trace id — it is the only handle a user has on a failure");
        var traceId = traceValues!.Single();

        // 32 lowercase hex characters: a W3C trace id, not a Guid and not an empty string. The
        // failure this catches is a header that is present and blank, which reads as working
        // right up until someone tries to look one up.
        traceId.Should().MatchRegex("^[0-9a-f]{32}$");

        response.Headers.TryGetValues(AuditMiddleware.CorrelationHeader, out var requestValues).Should().BeTrue();
        var requestId = requestValues!.Single();

        // The chain writer drains the outbox on a timer, so the row reaches audit_events a
        // moment after the response. Poll rather than sleep once — a fixed delay is either
        // flaky or slow, and usually both.
        var (actions, correlationId) = await PollForTraceAsync(traceId, ct);

        actions.Should().NotBeEmpty(
            "the audit rows for this request must carry the same trace id the caller was given");
        actions.Should().Contain("tenant.created");

        // The two ids are different things — one identifies the distributed trace, the other
        // the browser action — and both must resolve, because a user may quote either.
        correlationId.Should().Be(requestId);
    }

    private async Task<(List<string> Actions, string? CorrelationId)> PollForTraceAsync(
        string traceId, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (true)
        {
            await using var conn = new NpgsqlConnection(fixture.PostgresConnectionString);
            await conn.OpenAsync(ct);
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                SELECT "Action", "CorrelationId"
                FROM audit.audit_events
                WHERE "TraceId" = @trace
                """;
            cmd.Parameters.AddWithValue("trace", traceId);

            var actions = new List<string>();
            string? correlationId = null;
            await using (var reader = await cmd.ExecuteReaderAsync(ct))
            {
                while (await reader.ReadAsync(ct))
                {
                    actions.Add(reader.GetString(0));
                    correlationId ??= reader.IsDBNull(1) ? null : reader.GetString(1);
                }
            }

            if (actions.Count > 0 || DateTime.UtcNow > deadline)
            {
                return (actions, correlationId);
            }
            await Task.Delay(500, ct);
        }
    }
}
