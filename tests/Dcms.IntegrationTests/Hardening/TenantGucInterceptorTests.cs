using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Dcms.IntegrationTests.Tenancy;
using Dcms.Shared.Data.Rls;
using Dcms.Shared.Data.Tenancy;
using Dcms.Shared.Kernel.Abstractions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;

namespace Dcms.IntegrationTests.Hardening;

/// <summary>
/// ADR 0015 phase 2: the interceptor that tells Postgres whose request this is.
///
/// <para>Every query here calls <c>IgnoreQueryFilters()</c> on purpose. With EF's predicate gone,
/// whatever narrowing is left is the database's — so these prove the enforcement, not the ORM.
/// They run as <c>dcms_app</c>, the role the services will connect as, through a real
/// <see cref="TenancyDbContext"/>.</para>
///
/// <para>The one that matters most is the pooled-connection test. A session GUC that outlived
/// its request would hand the next request on that connection the previous tenant's rows: a
/// cross-tenant read introduced by the control meant to prevent one.</para>
/// </summary>
[Collection(AdminApiCollection.Name)]
public class TenantGucInterceptorTests(AdminApiFixture fixture)
{
    private const string AppRole = "dcms_app";
    private const string AppPassword = "dcms-app-test";

    [DockerFact]
    public async Task The_database_holds_the_tenant_even_with_the_EF_filter_ignored()
    {
        var ct = TestContext.Current.CancellationToken;
        var (a, b) = await TwoTenantsAsync(ct);
        var tenant = new SettableTenant();

        await using var db = Context(tenant, AppConnection());

        tenant.TenantId = a;
        (await TenantIdsAsync(db, ct)).Should().BeEquivalentTo([a]);

        tenant.TenantId = b;
        (await TenantIdsAsync(db, ct)).Should().BeEquivalentTo([b]);

        tenant.TenantId = null;
        (await TenantIdsAsync(db, ct)).Should().BeEmpty("a request that lost its tenant sees nothing");
    }

    [DockerFact]
    public async Task A_platform_scope_widens_and_narrows_on_a_connection_held_open_by_a_transaction()
    {
        var ct = TestContext.Current.CancellationToken;
        var (a, b) = await TwoTenantsAsync(ct);
        var tenant = new SettableTenant { TenantId = a };

        await using var db = Context(tenant, AppConnection());
        // The connection is opened once here and not again: only the per-command re-check can
        // move the GUC after this point.
        await using var transaction = await db.Database.BeginTransactionAsync(ct);

        (await TenantIdsAsync(db, ct)).Should().BeEquivalentTo([a]);

        using (RlsScope.Platform())
        {
            (await TenantIdsAsync(db, ct)).Should().Contain([a, b]);
        }

        (await TenantIdsAsync(db, ct)).Should().BeEquivalentTo([a], "leaving the scope has to narrow again");
    }

    [DockerFact]
    public async Task A_tenant_scope_overrides_the_request_and_narrows_inside_a_platform_one()
    {
        var ct = TestContext.Current.CancellationToken;
        var (a, b) = await TwoTenantsAsync(ct);

        // No ambient tenant at all: the shape of a consumer handling one tenant's event.
        await using var db = Context(new SettableTenant(), AppConnection());

        using (RlsScope.Platform())
        {
            (await TenantIdsAsync(db, ct)).Should().Contain([a, b]);

            using (RlsScope.Tenant(b))
            {
                (await TenantIdsAsync(db, ct)).Should().BeEquivalentTo([b],
                    "the per-item step of a scan is one tenant's work, and the database has to hold it there");
            }
        }

        (await TenantIdsAsync(db, ct)).Should().BeEmpty();
    }

    [DockerFact]
    public async Task A_raw_command_on_a_connection_EF_opened_carries_the_tenant()
    {
        var ct = TestContext.Current.CancellationToken;
        var (a, _) = await TwoTenantsAsync(ct);

        // The shape ContentListQueries and TagQueries use: EF opens, the SQL is hand-written.
        // A raw OpenAsync would skip the interceptor and see nothing.
        await using var db = Context(new SettableTenant { TenantId = a }, AppConnection());
        await db.Database.OpenConnectionAsync(ct);
        try
        {
            await using var command = db.Database.GetDbConnection().CreateCommand();
            command.CommandText = "SELECT DISTINCT \"TenantId\" FROM tenancy.tenant_memberships";
            var seen = new List<Guid>();
            await using (var reader = await command.ExecuteReaderAsync(ct))
            {
                while (await reader.ReadAsync(ct))
                {
                    seen.Add(reader.GetGuid(0));
                }
            }
            seen.Should().BeEquivalentTo([a]);
        }
        finally
        {
            await db.Database.CloseConnectionAsync();
        }
    }

    [DockerFact]
    public async Task Nothing_set_for_one_request_survives_into_the_next_use_of_the_pooled_connection()
    {
        var ct = TestContext.Current.CancellationToken;
        var (a, _) = await TwoTenantsAsync(ct);

        // A pool of one, private to this test (pools are keyed by connection string), so the
        // raw connection below is provably the same server backend the context just used.
        var connection = new NpgsqlConnectionStringBuilder(AppConnection())
        {
            MaxPoolSize = 1,
            ApplicationName = "guc-leak-" + Guid.NewGuid().ToString("N")[..8],
        }.ConnectionString;

        int backend;
        await using (var db = Context(new SettableTenant { TenantId = a }, connection))
        {
            using (RlsScope.Platform())
            {
                backend = await db.Database.SqlQueryRaw<int>("SELECT pg_backend_pid() AS \"Value\"").SingleAsync(ct);
            }
        }

        await using var next = new NpgsqlConnection(connection);
        await next.OpenAsync(ct);
        await using var command = next.CreateCommand();
        command.CommandText = """
            SELECT pg_backend_pid(),
                   coalesce(current_setting('app.tenant_id', true), ''),
                   coalesce(current_setting('app.scope', true), '')
            """;
        await using var reader = await command.ExecuteReaderAsync(ct);
        (await reader.ReadAsync(ct)).Should().BeTrue();

        reader.GetInt32(0).Should().Be(backend, "otherwise this proves nothing about reuse");
        reader.GetString(1).Should().BeEmpty("the previous request's tenant must not be inherited");
        reader.GetString(2).Should().BeEmpty("the previous request's platform scope must not be inherited");
    }

    [DockerFact]
    public async Task A_write_for_the_scoped_tenant_is_accepted_and_stays_in_it()
    {
        var ct = TestContext.Current.CancellationToken;
        var (a, b) = await TwoTenantsAsync(ct);
        var email = $"{Guid.NewGuid():N}@dcms.test";

        await using (var db = Context(new SettableTenant { TenantId = a }, AppConnection()))
        {
            db.Memberships.Add(new TenantMembership { Id = Guid.NewGuid(), UserId = Guid.NewGuid(), Email = email });
            await db.SaveChangesAsync(ct);
        }

        await using var other = Context(new SettableTenant { TenantId = b }, AppConnection());
        (await other.Memberships.IgnoreQueryFilters().AnyAsync(m => m.Email == email, ct))
            .Should().BeFalse("tenant B's connection must not see tenant A's new row");
    }

    private static TenancyDbContext Context(ITenantContext tenant, string connection)
        => new(new DbContextOptionsBuilder<TenancyDbContext>()
            .UseNpgsql(connection)
            .AddInterceptors(new TenantGucInterceptor(tenant, NullLogger<TenantGucInterceptor>.Instance))
            .Options, tenant);

    private static async Task<List<Guid>> TenantIdsAsync(TenancyDbContext db, CancellationToken ct)
        => await db.Memberships.IgnoreQueryFilters().Select(m => m.TenantId).Distinct().ToListAsync(ct);

    private string AppConnection() => new NpgsqlConnectionStringBuilder(fixture.PostgresConnectionString)
    {
        Username = AppRole,
        Password = AppPassword,
    }.ConnectionString;

    private async Task<(Guid A, Guid B)> TwoTenantsAsync(CancellationToken ct)
    {
        await using (var owner = new NpgsqlConnection(fixture.PostgresConnectionString))
        {
            await owner.OpenAsync(ct);
            await using var command = owner.CreateCommand();
            command.CommandText = $$"""
                DO $$ BEGIN
                    IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = '{{AppRole}}') THEN
                        CREATE ROLE {{AppRole}} LOGIN PASSWORD '{{AppPassword}}' NOSUPERUSER NOBYPASSRLS;
                    END IF;
                END $$;
                GRANT USAGE ON SCHEMA tenancy TO {{AppRole}};
                GRANT SELECT, INSERT, UPDATE, DELETE ON tenancy.tenant_memberships TO {{AppRole}};
                """;
            await command.ExecuteNonQueryAsync(ct);
        }

        var client = fixture.Factory.CreateClient();
        return (await CreateTenantAsync(client, "guc-a-", ct), await CreateTenantAsync(client, "guc-b-", ct));
    }

    private static async Task<Guid> CreateTenantAsync(HttpClient client, string prefix, CancellationToken ct)
    {
        var slug = prefix + Guid.NewGuid().ToString("N")[..8];
        var owner = Guid.NewGuid();
        var response = await client.SendAsync(new HttpRequestMessage(HttpMethod.Post, "/api/admin/tenants")
        {
            Headers = { { "X-Test-Sub", Guid.NewGuid().ToString() }, { "X-Test-Roles", "SuperAdmin" } },
            Content = JsonContent.Create(new { slug, name = slug, ownerUserId = owner, ownerEmail = $"{owner:N}@dcms.test" }),
        }, ct);
        response.StatusCode.Should().Be(HttpStatusCode.Created);
        return (await response.Content.ReadFromJsonAsync<JsonElement>(ct)).GetProperty("tenantId").GetGuid();
    }

    private sealed class SettableTenant : ITenantContext
    {
        public Guid? TenantId { get; set; }
        public string? TenantSlug => null;
    }
}
