using System.Security.Cryptography;
using System.Text;
using Dcms.Shared.Audit;
using Dcms.Shared.Data.Audit;
using Dcms.Shared.Kernel.Abstractions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Testcontainers.PostgreSql;

namespace Dcms.IntegrationTests.Audit;

/// <summary>
/// The chain against a real Postgres: partitioning, the append transaction, idempotent
/// redelivery, and — the point of the whole exercise — whether an altered row is actually
/// detected.
/// </summary>
public sealed class AuditChainTests : IAsyncLifetime
{
    private readonly PostgreSqlContainer _postgres = TestPostgres.Build();
    private AuditDbContext _db = null!;
    private AuditChainAppender _appender = null!;
    private AuditChainVerifier _verifier = null!;

    private static readonly Guid Tenant = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000001");

    public async ValueTask InitializeAsync()
    {
        await _postgres.StartAsync();

        var options = new DbContextOptionsBuilder<AuditDbContext>()
            .UseNpgsql(_postgres.GetConnectionString())
            .Options;

        _db = new AuditDbContext(options);
        await _db.Database.MigrateAsync();
        await AuditSchemaConfigurator.ApplyAsync(_db, NullLogger.Instance);

        var keys = new FixedKey();
        _appender = new AuditChainAppender(_db, keys, new SystemClock());
        _verifier = new AuditChainVerifier(_db, keys, new AuditMetrics(new TestMeterFactory()));
    }

    public async ValueTask DisposeAsync()
    {
        await _db.DisposeAsync();
        await _postgres.DisposeAsync();
    }

    /// <summary>
    /// The round trip, with payloads that storage is tempted to rewrite.
    ///
    /// <para>Two bugs reached production behind this gap. <c>OccurredAt</c> was hashed at .NET's
    /// 100ns tick precision and stored as microseconds, losing the seventh digit; and the payload
    /// columns were <c>jsonb</c>, which reorders object keys by length and rewrites separators.
    /// Both meant the bytes hashed on the way in were not the bytes read back, so verification
    /// reported "this row was altered after it was written" — a tampering alarm raised by
    /// storage normalisation.</para>
    ///
    /// <para>The plain chain test above would have caught the timestamp half. It would <i>not</i>
    /// have caught the jsonb half, because its records carry no metadata and <c>{}</c> survives
    /// jsonb unchanged. Hence multi-key metadata and a field diff here, deliberately in an order
    /// jsonb would not choose for itself.</para>
    /// </summary>
    [DockerFact]
    public async Task Records_with_payloads_still_verify_after_a_storage_round_trip()
    {
        var rich = Event("content.published") with
        {
            // Keys ordered so that jsonb's own ordering (by length, then bytes) differs from
            // this one — otherwise the reordering would be invisible.
            Metadata = new Dictionary<string, object?>
            {
                ["permissions_granted"] = new[] { "audit:read", "audit:export" },
                ["permission"] = "content:publish",
                ["reason"] = "verifying that storage does not rewrite what was hashed",
                ["count"] = 42,
            },
            Changes =
            [
                new AuditFieldChange("Status", "draft", "published", false),
                new AuditFieldChange("ApiKeyCiphertext", null, null, true),
            ],
        };

        await _appender.AppendAsync([rich, Event("content.unpublished")]);

        var period = AuditChainAppender.PeriodOf(DateTimeOffset.UtcNow);
        var result = await _verifier.VerifySegmentAsync(Tenant, period);

        result.Valid.Should().BeTrue(result.Reason);

        // And the stored text must be byte-identical to what the writer produced — the moment
        // storage reformats it, the hash over it is meaningless.
        var stored = await _db.Events.AsNoTracking().OrderBy(e => e.Seq).FirstAsync();
        stored.MetadataJson.Should().Contain("\"permissions_granted\"");
        stored.MetadataJson.Should().NotContain("\": \"", "storage must not reformat the payload it was handed");
    }

    /// <summary>
    /// A sub-microsecond timestamp is the exact shape that broke production: .NET keeps the
    /// seventh fractional digit, Postgres does not.
    /// </summary>
    [DockerFact]
    public async Task A_record_with_sub_microsecond_precision_verifies()
    {
        // 100ns-level precision that timestamptz cannot represent. A seventh digit of 4 is the
        // case that fails; a seventh digit of 0 would survive on its own and hide the bug.
        var awkward = Event("site.created") with
        {
            OccurredAt = new DateTimeOffset(DateTime.UtcNow.Date, TimeSpan.Zero)
                .AddHours(9).AddTicks(7_928_474),
        };

        await _appender.AppendAsync([awkward]);

        var result = await _verifier.VerifySegmentAsync(Tenant, AuditChainAppender.PeriodOf(awkward.OccurredAt));
        result.Valid.Should().BeTrue(result.Reason);
    }

    [DockerFact]
    public async Task Appended_records_form_a_verifiable_chain()
    {
        await _appender.AppendAsync([Event("site.created"), Event("site.updated"), Event("site.deleted")]);

        var period = AuditChainAppender.PeriodOf(DateTimeOffset.UtcNow);
        var result = await _verifier.VerifySegmentAsync(Tenant, period);

        result.Valid.Should().BeTrue(result.Reason);
        result.Checked.Should().Be(3);

        var rows = await _db.Events.AsNoTracking().OrderBy(e => e.Seq).ToListAsync();
        rows.Select(r => r.Seq).Should().Equal(1, 2, 3);
        // The first record starts the chain, so it has nothing to link back to.
        rows[0].PrevHash.Should().BeNull();
        rows[1].PrevHash.Should().Equal(rows[0].Hash);
        rows[2].PrevHash.Should().Equal(rows[1].Hash);
    }

    [DockerFact]
    public async Task Re_appending_the_same_event_is_a_no_op()
    {
        // At-least-once delivery is a fact of life for the two services that publish over NATS,
        // and a retried outbox drain does the same thing. A second copy must not appear, and
        // the sequence must not advance for one.
        var @event = Event("site.published");

        (await _appender.AppendAsync([@event])).Should().Be(1);
        (await _appender.AppendAsync([@event])).Should().Be(0);

        (await _db.Events.CountAsync()).Should().Be(1);

        var head = await _db.ChainHeads.AsNoTracking().SingleAsync();
        head.Seq.Should().Be(1);
    }

    [DockerFact]
    public async Task Records_for_different_tenants_are_chained_independently()
    {
        var other = Guid.Parse("bbbbbbbb-0000-0000-0000-000000000002");

        await _appender.AppendAsync([Event("site.created"), Event("site.created", other)]);

        var period = AuditChainAppender.PeriodOf(DateTimeOffset.UtcNow);
        (await _verifier.VerifySegmentAsync(Tenant, period)).Checked.Should().Be(1);
        (await _verifier.VerifySegmentAsync(other, period)).Checked.Should().Be(1);

        // Each tenant's chain starts at 1: one tenant's activity must not be inferable from
        // gaps in another's sequence.
        var seqs = await _db.Events.AsNoTracking().Select(e => e.Seq).ToListAsync();
        seqs.Should().AllSatisfy(s => s.Should().Be(1));
    }

    [DockerFact]
    public async Task The_append_only_trigger_rejects_a_direct_update()
    {
        await _appender.AppendAsync([Event("tenant.purged")]);

        var update = async () => await _db.Database.ExecuteSqlRawAsync(
            """UPDATE audit.audit_events SET "Action" = 'nothing.happened';""");

        await update.Should().ThrowAsync<Exception>()
            .WithMessage("*append-only*");
    }

    [DockerFact]
    public async Task An_altered_record_is_detected_even_when_the_trigger_is_disabled()
    {
        // This is the scenario that matters. Seven of the eight services connect as a Postgres
        // superuser, so an attacker holding the application's credentials can simply turn the
        // trigger off. What they cannot do is recompute the HMAC without the Vault-held key.
        await _appender.AppendAsync([Event("site.created"), Event("tenant.purged"), Event("site.deleted")]);

        await _db.Database.ExecuteSqlRawAsync("""
            ALTER TABLE audit.audit_events DISABLE TRIGGER audit_events_append_only;
            UPDATE audit.audit_events SET "Action" = 'site.updated' WHERE "Action" = 'tenant.purged';
            ALTER TABLE audit.audit_events ENABLE TRIGGER audit_events_append_only;
            """);

        var period = AuditChainAppender.PeriodOf(DateTimeOffset.UtcNow);
        var result = await _verifier.VerifySegmentAsync(Tenant, period);

        result.Valid.Should().BeFalse();
        result.FirstBadSeq.Should().Be(2);
        result.Reason.Should().Contain("altered");
    }

    [DockerFact]
    public async Task A_deleted_record_is_detected_as_a_gap()
    {
        await _appender.AppendAsync([Event("site.created"), Event("tenant.purged"), Event("site.deleted")]);

        await _db.Database.ExecuteSqlRawAsync("""
            ALTER TABLE audit.audit_events DISABLE TRIGGER audit_events_append_only;
            DELETE FROM audit.audit_events WHERE "Seq" = 2;
            ALTER TABLE audit.audit_events ENABLE TRIGGER audit_events_append_only;
            """);

        var period = AuditChainAppender.PeriodOf(DateTimeOffset.UtcNow);
        var result = await _verifier.VerifySegmentAsync(Tenant, period);

        result.Valid.Should().BeFalse();
        result.Reason.Should().Contain("missing");
    }

    [DockerFact]
    public async Task Records_land_in_the_partition_for_their_month()
    {
        await _appender.AppendAsync([Event("site.created")]);

        var partition = await _db.Database
            .SqlQuery<string>($"""
                SELECT c.relname AS "Value"
                FROM audit.audit_events e
                JOIN pg_class c ON c.oid = e.tableoid
                LIMIT 1
                """)
            .SingleAsync();

        // Not the default partition — if it were, retention could not drop it by month and the
        // pre-creation in AuditSchemaConfigurator is not doing its job.
        partition.Should().Be($"audit_events_{DateTime.UtcNow:yyyy}m{DateTime.UtcNow:MM}");
    }

    private static AuditEvent Event(string action, Guid? tenantId = null) => new()
    {
        TenantId = tenantId ?? Tenant,
        Action = action,
        Actor = new AuditActor(ActorKind.User, Guid.NewGuid(), null, "tester@example.com", AuditAttribution.Direct),
        ServiceName = "admin-api",
        ServiceInstance = "test-instance",
        ProducerSeq = 1,
    };

    private sealed class FixedKey : IAuditChainKeyProvider
    {
        private readonly byte[] _key = SHA256.HashData(Encoding.UTF8.GetBytes("integration-test-key"));
        public byte[] GetKey() => _key;
    }
}
