using System.Security.Cryptography;
using System.Text;
using Dcms.Shared.Data.Audit;

namespace Dcms.UnitTests.Audit;

/// <summary>
/// The canonical form is the foundation of the whole integrity claim: if two different records
/// can produce the same bytes, or if the same record can produce different bytes on different
/// days, the chain proves nothing. These tests exist to make either regression loud.
/// </summary>
public class AuditCanonicalizerTests
{
    private static readonly byte[] Key = SHA256.HashData(Encoding.UTF8.GetBytes("test-key"));

    private static AuditEventRow Row(Action<AuditEventRow>? tweak = null)
    {
        var row = new AuditEventRow
        {
            Id = Guid.Parse("11111111-1111-1111-1111-111111111111"),
            EventId = Guid.Parse("22222222-2222-2222-2222-222222222222"),
            OccurredAt = new DateTimeOffset(2026, 8, 20, 12, 0, 0, TimeSpan.Zero),
            ChainKey = Guid.Parse("33333333-3333-3333-3333-333333333333"),
            Period = new DateOnly(2026, 8, 1),
            Seq = 1,
            TenantId = Guid.Parse("33333333-3333-3333-3333-333333333333"),
            Action = "site.deleted",
            Category = "tenantstate",
            Outcome = "success",
            ActorKind = "user",
            ActorAttribution = "direct",
            ServiceName = "admin-api",
            ServiceInstance = "instance-a",
            ProducerSeq = 7,
            MetadataJson = "{}",
            SchemaVersion = 1,
        };
        tweak?.Invoke(row);
        return row;
    }

    [Fact]
    public void Canonical_form_is_stable_for_the_same_record()
    {
        AuditCanonicalizer.Canonicalize(Row())
            .Should().Equal(AuditCanonicalizer.Canonicalize(Row()));
    }

    [Fact]
    public void Changing_any_recorded_field_changes_the_hash()
    {
        var baseline = AuditCanonicalizer.ComputeHash(Row(), null, Key);

        AuditCanonicalizer.ComputeHash(Row(r => r.Action = "site.created"), null, Key)
            .Should().NotEqual(baseline);
        AuditCanonicalizer.ComputeHash(Row(r => r.ActorId = Guid.NewGuid()), null, Key)
            .Should().NotEqual(baseline);
        AuditCanonicalizer.ComputeHash(Row(r => r.MetadataJson = """{"x":1}"""), null, Key)
            .Should().NotEqual(baseline);
        AuditCanonicalizer.ComputeHash(Row(r => r.Outcome = "denied"), null, Key)
            .Should().NotEqual(baseline);
    }

    [Fact]
    public void Field_boundaries_cannot_be_shifted()
    {
        // Without length prefixes, ("ab", "c") and ("a", "bc") concatenate identically and an
        // attacker could move the boundary between two adjacent fields while keeping the hash.
        var left = AuditCanonicalizer.ComputeHash(
            Row(r => { r.ResourceType = "ab"; r.ResourceId = "c"; }), null, Key);
        var right = AuditCanonicalizer.ComputeHash(
            Row(r => { r.ResourceType = "a"; r.ResourceId = "bc"; }), null, Key);

        left.Should().NotEqual(right);
    }

    [Fact]
    public void Null_is_distinguishable_from_empty()
    {
        var nullValue = AuditCanonicalizer.ComputeHash(Row(r => r.ResourceId = null), null, Key);
        var emptyValue = AuditCanonicalizer.ComputeHash(Row(r => r.ResourceId = string.Empty), null, Key);

        nullValue.Should().NotEqual(emptyValue);
    }

    [Fact]
    public void The_same_instant_in_a_different_offset_canonicalizes_identically()
    {
        // Otherwise a producer running in a non-UTC offset would write records that fail to
        // verify anywhere else.
        var utc = Row(r => r.OccurredAt = new DateTimeOffset(2026, 8, 20, 12, 0, 0, TimeSpan.Zero));
        var shifted = Row(r => r.OccurredAt = new DateTimeOffset(2026, 8, 20, 14, 0, 0, TimeSpan.FromHours(2)));

        AuditCanonicalizer.Canonicalize(utc).Should().Equal(AuditCanonicalizer.Canonicalize(shifted));
    }

    [Fact]
    public void A_different_key_produces_a_different_hash()
    {
        // The point of keying: someone who can rewrite the table cannot recompute the chain.
        var other = SHA256.HashData(Encoding.UTF8.GetBytes("attacker-key"));

        AuditCanonicalizer.ComputeHash(Row(), null, Key)
            .Should().NotEqual(AuditCanonicalizer.ComputeHash(Row(), null, other));
    }

    [Fact]
    public void The_predecessor_hash_is_part_of_the_hash()
    {
        var first = AuditCanonicalizer.ComputeHash(Row(), null, Key);
        var linked = AuditCanonicalizer.ComputeHash(Row(), first, Key);

        linked.Should().NotEqual(first);
    }

    // ---- precision ----------------------------------------------------------------
    //
    // OccurredAt is hashed, and Postgres timestamptz keeps microseconds while a .NET tick is
    // 100ns. Hashing the seventh digit and then verifying against a value that no longer has
    // it made records fail as "altered after it was written" — and only sometimes, because a
    // seventh digit of zero survives. These pin the truncation that fixes it.

    [Fact]
    public void Sub_microsecond_precision_is_dropped_before_hashing()
    {
        var raw = new DateTimeOffset(2026, 8, 21, 11, 32, 12, TimeSpan.Zero).AddTicks(7_928_474);
        var stored = AuditChainAppender.StorablePrecision(raw);

        // 7928474 ticks = 792.8474 ms; Postgres keeps 792847 microseconds.
        (stored.Ticks % TimeSpan.TicksPerMicrosecond).Should().Be(0);
        stored.Should().Be(new DateTimeOffset(2026, 8, 21, 11, 32, 12, TimeSpan.Zero).AddTicks(7_928_470));
    }

    [Fact]
    public void Truncating_an_already_storable_instant_changes_nothing()
    {
        // Idempotent, so re-appending a redelivered record hashes identically.
        var once = AuditChainAppender.StorablePrecision(DateTimeOffset.UtcNow);
        AuditChainAppender.StorablePrecision(once).Should().Be(once);
    }

    [Fact]
    public void Truncation_never_rounds_up_across_a_month_boundary()
    {
        // Rounding could carry an instant into the next microsecond and, at the last tick of a
        // month, into the next partition — and so into a different chain.
        var lastTick = new DateTimeOffset(2026, 8, 31, 23, 59, 59, TimeSpan.Zero).AddTicks(9_999_999);
        var stored = AuditChainAppender.StorablePrecision(lastTick);

        AuditChainAppender.PeriodOf(stored).Should().Be(new DateOnly(2026, 8, 1));
    }

    [Fact]
    public void A_truncated_instant_hashes_the_same_as_its_round_tripped_self()
    {
        // The property that actually matters: hash before the write, verify after it, get the
        // same answer. Postgres is simulated here by dropping the sub-microsecond ticks.
        var raw = DateTimeOffset.UtcNow;
        var atWrite = Row(r => r.OccurredAt = AuditChainAppender.StorablePrecision(raw));
        var asReadBack = Row(r => r.OccurredAt = DropSubMicrosecond(atWrite.OccurredAt));

        AuditCanonicalizer.ComputeHash(atWrite, null, Key)
            .Should().Equal(AuditCanonicalizer.ComputeHash(asReadBack, null, Key));
    }

    private static DateTimeOffset DropSubMicrosecond(DateTimeOffset value) =>
        value.AddTicks(-(value.Ticks % TimeSpan.TicksPerMicrosecond));
}
