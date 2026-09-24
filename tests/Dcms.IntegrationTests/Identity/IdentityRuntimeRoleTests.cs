using System.Net;
using Dcms.Shared.Audit;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.DependencyInjection;

namespace Dcms.IntegrationTests.Identity;

/// <summary>
/// ADR 0015 phase 5: identity runs as <c>dcms_identity</c>, which holds an INSERT on the audit
/// outbox and nothing else in the audit schema. An audit write that fails does not fail the
/// request, so every other identity test passes with that grant missing, while production
/// quietly stops recording sign-ins. This is the check that notices.
/// </summary>
[Collection(IdentityCollection.Name)]
public sealed class IdentityRuntimeRoleTests(IdentityAppFixture fixture)
{
    [DockerFact]
    public async Task An_issued_token_is_recorded_in_the_audit_outbox()
    {
        var ct = TestContext.Current.CancellationToken;
        var since = DateTimeOffset.UtcNow.AddSeconds(-1);

        var token = await fixture.Factory.CreateClient().PostAsync("/connect/token", new FormUrlEncodedContent(
            new Dictionary<string, string>
            {
                ["grant_type"] = "client_credentials",
                ["client_id"] = "dcms-admin-api",
                ["client_secret"] = "dcms-admin-api-dev-secret",
                ["scope"] = "dcms.ai",
            }), ct);
        token.StatusCode.Should().Be(HttpStatusCode.OK);

        await using var owner = new Npgsql.NpgsqlConnection(fixture.OwnerConnectionString);
        await owner.OpenAsync(ct);
        await using var query = owner.CreateCommand();
        query.CommandText = """
            SELECT count(*) FROM audit.audit_outbox
            WHERE "OccurredAt" >= @since AND "PayloadJson"::text LIKE '%' || @action || '%'
            """;
        query.Parameters.AddWithValue("since", since);
        query.Parameters.AddWithValue("action", AuditActions.TokenIssued);

        ((long)(await query.ExecuteScalarAsync(ct))!).Should().BeGreaterThan(0,
            "identity's audit records reach the outbox under dcms_identity");
    }

    /// <summary>
    /// The shared key ring, which protects identity's sign-in cookies and the Forgejo password
    /// outbox. No other test in this collection touches it: the cookie tests bring a key ring
    /// of their own.
    /// </summary>
    [DockerFact]
    public void The_key_ring_is_readable_and_writable_as_the_runtime_role()
    {
        var protector = fixture.Factory.Services
            .GetRequiredService<IDataProtectionProvider>()
            .CreateProtector($"rls-phase5-{Guid.NewGuid():N}");

        protector.Unprotect(protector.Protect("round trip")).Should().Be("round trip");
    }
}
