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
/// Sealing, retention and omission detection against a real Postgres.
///
/// <para>These three exist to protect the property that survives a partition drop. The chain
/// itself is tested next door; what is tested here is what remains when the rows are gone, and
/// — the one that matters most — that nothing is ever dropped before it has been anchored.</para>
/// </summary>
public sealed class AuditSealingTests : IAsyncLifetime
{
    private readonly PostgreSqlContainer _postgres = TestPostgres.Build();
    private AuditDbContext _db = null!;
    private AuditChainAppender _appender = null!;
    private AuditChainVerifier _verifier = null!;
    private AuditChainSealer _sealer = null!;
    private FixedKey _keys = null!;

    private static readonly Guid Tenant = Guid.Parse("bbbbbbbb-0000-0000-0000-000000000001");

    /// <summary>
    /// Two months back, so the period is unambiguously "finished" whichever day of the month
    /// the suite runs on. One month back would be finished too, but the extra distance means a
    /// run that straddles midnight on the first cannot flip the answer.
    /// </summary>
    private static DateOnly LastPeriod => AuditChainAppender.PeriodOf(DateTimeOffset.UtcNow).AddMonths(-2);

    public async ValueTask InitializeAsync()
    {
        await _postgres.StartAsync();

        var options = new DbContextOptionsBuilder<AuditDbContext>()
            .UseNpgsql(_postgres.GetConnectionString())
            .Options;

        _db = new AuditDbContext(options);
        await _db.Database.MigrateAsync();
        await AuditSchemaConfigurator.ApplyAsync(_db, NullLogger.Instance);

        // EnsurePartitionsAsync only creates the current month and the next few; a past month
        // has to be made by hand here, because rows landing in the default partition would make
        // the drop untestable — which is itself why the maintenance worker runs hourly.
        await CreatePartitionAsync(LastPeriod);

        _keys = new FixedKey();
        _appender = new AuditChainAppender(_db, _keys, new SystemClock());
        _verifier = new AuditChainVerifier(_db, _keys, new AuditMetrics(new TestMeterFactory()));
        _sealer = new AuditChainSealer(_db, _verifier, _keys, NullLogger<AuditChainSealer>.Instance);
    }

    public async ValueTask DisposeAsync()
    {
        await _db.DisposeAsync();
        await _postgres.DisposeAsync();
    }

    [DockerFact]
    public async Task Sealing_a_finished_month_writes_an_authentic_anchor()
    {
        await AppendPastAsync(3);

        var sealedCount = await _sealer.SealCompletedAsync();
        sealedCount.Should().Be(1);

        var anchor = await _db.ChainAnchors.AsNoTracking()
            .SingleAsync(a => a.ChainKey == Tenant && a.Period == LastPeriod);

        anchor.RowCount.Should().Be(3);
        anchor.FirstSeq.Should().Be(1);
        anchor.LastSeq.Should().Be(3);
        _sealer.IsAuthentic(anchor).Should().BeTrue();
    }

    [DockerFact]
    public async Task An_anchor_edited_to_agree_with_a_rewritten_chain_stops_being_authentic()
    {
        await AppendPastAsync(2);
        await _sealer.SealCompletedAsync();

        var anchor = await _db.ChainAnchors.AsNoTracking()
            .SingleAsync(a => a.ChainKey == Tenant && a.Period == LastPeriod);

        // The single move an attacker with full database write access would have to make, and
        // the one the Vault key denies them.
        anchor.RowCount = 1;

        _sealer.IsAuthentic(anchor).Should().BeFalse();
    }

    [DockerFact]
    public async Task Sealing_is_idempotent()
    {
        await AppendPastAsync(2);

        (await _sealer.SealCompletedAsync()).Should().Be(1);
        (await _sealer.SealCompletedAsync()).Should().Be(0);

        var anchors = await _db.ChainAnchors.AsNoTracking()
            .CountAsync(a => a.ChainKey == Tenant && a.Period == LastPeriod);

        // A second anchor for one segment would make the pair meaningless: neither could be
        // shown to be the original.
        anchors.Should().Be(1);
    }

    [DockerFact]
    public async Task The_current_month_is_never_sealed()
    {
        await _appender.AppendAsync([Event("site.created")]);

        await _sealer.SealCompletedAsync();

        var current = AuditChainAppender.PeriodOf(DateTimeOffset.UtcNow);
        var anchored = await _db.ChainAnchors.AsNoTracking().AnyAsync(a => a.Period == current);

        // An anchor over a month still being written to is contradicted by the next record
        // that lands in it.
        anchored.Should().BeFalse();
    }

    [DockerFact]
    public async Task A_broken_segment_is_refused_rather_than_anchored()
    {
        await AppendPastAsync(3);

        await TamperAsync("site.not.what.happened");

        var result = await _sealer.SealAsync(Tenant, LastPeriod);

        result.Should().BeFalse();
        (await _db.ChainAnchors.AsNoTracking().AnyAsync(a => a.Period == LastPeriod))
            .Should().BeFalse();
    }

    [DockerFact]
    public async Task Retention_drops_a_sealed_expired_partition_and_keeps_its_anchor()
    {
        await AppendPastAsync(3);

        var dropped = await RetentionFor(retentionDays: 1).ApplyAsync();
        dropped.Should().Be(1);

        // The rows go with the partition...
        var remaining = await _db.Events.AsNoTracking().CountAsync(e => e.Period == LastPeriod);
        remaining.Should().Be(0);

        // ...and the anchor, which is what still answers "was that month intact", does not.
        var anchor = await _db.ChainAnchors.AsNoTracking()
            .SingleAsync(a => a.ChainKey == Tenant && a.Period == LastPeriod);

        anchor.PartitionDroppedAt.Should().NotBeNull();
        anchor.RowCount.Should().Be(3);
        _sealer.IsAuthentic(anchor).Should().BeTrue();
    }

    [DockerFact]
    public async Task Retention_never_drops_a_partition_that_would_not_seal()
    {
        await AppendPastAsync(3);
        await TamperAsync("tampered");

        var dropped = await RetentionFor(retentionDays: 1).ApplyAsync();

        // The right way round: the segment that most needs looking at is the one that stays.
        dropped.Should().Be(0);
        (await _db.Events.AsNoTracking().CountAsync(e => e.Period == LastPeriod)).Should().Be(3);
    }

    [DockerFact]
    public async Task Retention_leaves_everything_inside_the_window_alone()
    {
        await AppendPastAsync(2);

        var dropped = await RetentionFor(retentionDays: 3650).ApplyAsync();

        dropped.Should().Be(0);
        (await _db.Events.AsNoTracking().CountAsync(e => e.Period == LastPeriod)).Should().Be(2);
    }

    [DockerFact]
    public async Task A_missing_producer_sequence_is_detected()
    {
        var now = DateTimeOffset.UtcNow;

        // Sequences 1, 2 and 4: the process issued four and delivered three. A chain over the
        // three that arrived verifies perfectly, which is the whole reason this check exists.
        await _appender.AppendAsync([
            Event("a", seq: 1, at: now.AddMinutes(-30)),
            Event("b", seq: 2, at: now.AddMinutes(-29)),
            Event("d", seq: 4, at: now.AddMinutes(-27)),
        ]);

        var gaps = await Detector().FindAsync(now.AddHours(-6), now.AddMinutes(-5));

        var gap = gaps.Should().ContainSingle().Subject;
        gap.ServiceInstance.Should().Be("test-instance");
        gap.FirstSeq.Should().Be(1);
        gap.LastSeq.Should().Be(4);
        gap.Delivered.Should().Be(3);
        gap.Missing.Should().Be(1);
    }

    [DockerFact]
    public async Task A_dense_producer_sequence_reports_nothing()
    {
        var now = DateTimeOffset.UtcNow;

        await _appender.AppendAsync([
            Event("a", seq: 1, at: now.AddMinutes(-30)),
            Event("b", seq: 2, at: now.AddMinutes(-29)),
            Event("c", seq: 3, at: now.AddMinutes(-28)),
        ]);

        var gaps = await Detector().FindAsync(now.AddHours(-6), now.AddMinutes(-5));

        gaps.Should().BeEmpty();
    }

    [DockerFact]
    public async Task An_unflushed_tail_is_not_reported_as_a_gap()
    {
        var now = DateTimeOffset.UtcNow;

        // Seq 1 settled; seq 3 landed seconds ago with seq 2 still in flight. Without the
        // settle period this reads as a lost record, and an alert that cries wolf every hour
        // is an alert nobody reads.
        await _appender.AppendAsync([
            Event("a", seq: 1, at: now.AddMinutes(-30)),
            Event("c", seq: 3, at: now.AddSeconds(-5)),
        ]);

        var gaps = await Detector().FindAsync(now.AddHours(-6), now.AddMinutes(-5));

        gaps.Should().BeEmpty();
    }

    private AuditGapDetector Detector() => new(_db, NullLogger<AuditGapDetector>.Instance);

    private AuditRetention RetentionFor(int retentionDays) => new(
        _db,
        _sealer,
        new AuditOptions { RetentionDays = retentionDays },
        new NullRecorder(),
        NullLogger<AuditRetention>.Instance);

    /// <summary>
    /// Alters a committed row the way the owner can — disabling the trigger first, which is
    /// precisely the superuser path the append-only control cannot stop and the chain exists
    /// to catch.
    /// </summary>
    private async Task TamperAsync(string replacementAction)
    {
        await _db.Database.ExecuteSqlRawAsync(
            "ALTER TABLE audit.audit_events DISABLE TRIGGER audit_events_append_only;");
        await _db.Database.ExecuteSqlRawAsync(
            """UPDATE audit.audit_events SET "Action" = {0} WHERE "ChainKey" = {1} AND "Seq" = 2;""",
            replacementAction, Tenant);
        await _db.Database.ExecuteSqlRawAsync(
            "ALTER TABLE audit.audit_events ENABLE TRIGGER audit_events_append_only;");
    }

    private Task AppendPastAsync(int count)
    {
        // Mid-month and mid-day, so no rounding can push a record into a neighbouring partition.
        var at = new DateTimeOffset(LastPeriod.Year, LastPeriod.Month, 15, 12, 0, 0, TimeSpan.Zero);

        var events = new AuditEvent[count];
        for (var i = 0; i < count; i++)
        {
            events[i] = Event($"site.action.{i}", seq: i + 1, at: at.AddMinutes(i));
        }

        return _appender.AppendAsync(events);
    }

    private async Task CreatePartitionAsync(DateOnly period)
    {
        var to = period.AddMonths(1);
        var sql = $"""
                   CREATE TABLE IF NOT EXISTS audit."audit_events_{period:yyyy}m{period:MM}"
                       PARTITION OF audit.audit_events
                       FOR VALUES FROM ('{period:yyyy-MM-dd}') TO ('{to:yyyy-MM-dd}');
                   """;
        await _db.Database.ExecuteSqlRawAsync(sql);

        // The chain's unique index lives per-partition, never on the parent, so a partition
        // made by hand here has to be given it the same way production does — otherwise these
        // tests run against a weaker table than the one they are meant to describe.
        await AuditSchemaConfigurator.EnsureChainIndexesAsync(_db, NullLogger.Instance);
    }

    private static AuditEvent Event(string action, long seq = 1, DateTimeOffset? at = null) => new()
    {
        TenantId = Tenant,
        Action = action,
        Actor = new AuditActor(ActorKind.User, Guid.NewGuid(), null, "tester@example.com", AuditAttribution.Direct),
        ServiceName = "admin-api",
        ServiceInstance = "test-instance",
        ProducerSeq = seq,
        OccurredAt = at ?? DateTimeOffset.UtcNow,
    };

    private sealed class FixedKey : IAuditChainKeyProvider
    {
        private readonly byte[] _key = SHA256.HashData(Encoding.UTF8.GetBytes("sealing-test-key"));
        public byte[] GetKey() => _key;
    }

    /// <summary>
    /// Retention records its own pruning, but where that record goes is not what these tests
    /// are about — and routing it through a real sink would make them depend on the outbox.
    /// </summary>
    private sealed class NullRecorder : IAuditRecorder
    {
        public AuditEntry? Declared => null;
        public IReadOnlyList<AuditEntry> Pending => [];
        public AuditEntry Record(string action) => new() { Action = action };
        public void Record(AuditEntry entry) { }
        public AuditEntry Declare(string action) => new() { Action = action };
        public void Discard(AuditEntry entry) { }
        public ValueTask RecordNowAsync(AuditEntry entry, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
        public IReadOnlyList<AuditEvent> Drain() => [];
        public ValueTask FlushAsync(CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
    }
}
