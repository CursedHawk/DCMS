using System.Buffers;
using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Dcms.Shared.Data.Audit;

/// <summary>
/// Turns a record into the exact bytes that get hashed, and chains it to its predecessor.
///
/// <para><b>Why this is hand-written rather than <c>JsonSerializer.Serialize</c>.</b> Property
/// order in System.Text.Json is not a stability contract — reordering a field in the record,
/// or a serializer upgrade, would change the bytes and silently invalidate every hash ever
/// computed. History would appear tampered with because somebody tidied a class. The field
/// list below is therefore explicit and append-only: <b>never reorder or remove an entry;
/// add new fields at the end and bump <see cref="CurrentVersion"/>.</b></para>
///
/// <para><b>Why lengths are prefixed.</b> Concatenating raw values lets
/// <c>("ab", "c")</c> and <c>("a", "bc")</c> produce identical bytes, so an attacker could
/// shift a boundary between two adjacent fields and keep the hash valid. A 4-byte length
/// before every field removes the ambiguity, and null is distinguished from empty.</para>
///
/// <para><b>Why HMAC rather than a plain digest.</b> Seven of the eight services connect to
/// Postgres as <c>dcms</c>, which the container entrypoint creates as a cluster superuser: it
/// bypasses RLS, ignores REVOKE, and can disable triggers. Anyone holding those credentials
/// could rewrite the table *and* recompute a plain SHA-256 chain over their rewrite. Keying
/// the chain with a secret only the chain writer holds is what makes the rewrite detectable.</para>
/// </summary>
public static class AuditCanonicalizer
{
    /// <summary>
    /// Bump when the field list — or what the stored values look like — changes. Stored per row,
    /// so a row always declares which rules it was hashed under.
    ///
    /// <para><b>v2:</b> <c>OccurredAt</c> is truncated to microseconds before hashing, and
    /// <c>MetadataJson</c>/<c>ChangesJson</c> are stored as <c>text</c> rather than <c>jsonb</c>.
    /// Under v1 both were hashed at a precision and in a byte form that Postgres then changed on
    /// write, so a v1 row can never be re-verified by anyone — the bytes it was hashed over no
    /// longer exist anywhere. The verifier reports such rows as written under an older algorithm,
    /// which is the honest answer: not "intact", but not "altered" either.</para>
    /// </summary>
    public const short CurrentVersion = 2;

    private const int NullMarker = -1;

    /// <summary>
    /// Computes the chain hash for a row: <c>HMAC(key, canonical(row) ‖ prevHash)</c>.
    /// <paramref name="previousHash"/> is null only for the first record of a chain.
    /// </summary>
    public static byte[] ComputeHash(AuditEventRow row, byte[]? previousHash, byte[] key)
    {
        ArgumentNullException.ThrowIfNull(row);
        ArgumentNullException.ThrowIfNull(key);

        var buffer = new ArrayBufferWriter<byte>();
        WriteCanonical(row, buffer);
        if (previousHash is not null)
        {
            WriteBytes(buffer, previousHash);
        }

        return HMACSHA256.HashData(key, buffer.WrittenSpan);
    }

    /// <summary>
    /// The canonical byte form. Exposed so tests can assert stability directly rather than
    /// only through a hash, where a regression is much harder to read.
    /// </summary>
    public static byte[] Canonicalize(AuditEventRow row)
    {
        var buffer = new ArrayBufferWriter<byte>();
        WriteCanonical(row, buffer);
        return buffer.WrittenSpan.ToArray();
    }

    // APPEND-ONLY. See the class remarks before touching this method.
    private static void WriteCanonical(AuditEventRow row, ArrayBufferWriter<byte> w)
    {
        WriteGuid(w, row.EventId);
        WriteInstant(w, row.OccurredAt);
        WriteGuid(w, row.TenantId);
        WriteGuid(w, row.ChainKey);
        WriteText(w, row.Period.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
        WriteInt64(w, row.Seq);

        WriteText(w, row.Action);
        WriteText(w, row.Category);
        WriteText(w, row.Outcome);
        WriteInt64(w, row.Severity);

        WriteText(w, row.ActorKind);
        WriteNullableGuid(w, row.ActorId);
        WriteText(w, row.ActorRef);
        WriteText(w, row.ActorDisplay);
        WriteText(w, row.ActorAttribution);
        WriteNullableGuid(w, row.SubjectUserId);

        WriteText(w, row.ResourceType);
        WriteText(w, row.ResourceId);
        WriteText(w, row.ResourceLabel);

        WriteText(w, row.ServiceName);
        WriteText(w, row.ServiceInstance);
        WriteInt64(w, row.ProducerSeq);

        WriteText(w, row.CorrelationId);
        WriteNullableGuid(w, row.CausationId);
        WriteText(w, row.TraceId);
        WriteText(w, row.SpanId);

        WriteText(w, row.HttpMethod);
        WriteText(w, row.RoutePattern);
        WriteText(w, row.StatusCode?.ToString(CultureInfo.InvariantCulture));
        WriteText(w, row.IpAddress);
        WriteBool(w, row.IpTrusted);
        WriteText(w, row.UserAgent);
        WriteBool(w, row.IsSandbox);

        WriteText(w, row.MetadataJson);
        WriteText(w, row.ChangesJson);
        WriteInt64(w, row.RedactionVersion);
        WriteInt64(w, row.SchemaVersion);
    }

    private static void WriteText(ArrayBufferWriter<byte> w, string? value)
    {
        if (value is null)
        {
            WriteLength(w, NullMarker);
            return;
        }
        var bytes = Encoding.UTF8.GetBytes(value);
        WriteBytes(w, bytes);
    }

    private static void WriteBytes(ArrayBufferWriter<byte> w, byte[] value)
    {
        WriteLength(w, value.Length);
        w.Write(value);
    }

    private static void WriteGuid(ArrayBufferWriter<byte> w, Guid value) =>
        WriteText(w, value.ToString("D", CultureInfo.InvariantCulture));

    private static void WriteNullableGuid(ArrayBufferWriter<byte> w, Guid? value) =>
        WriteText(w, value?.ToString("D", CultureInfo.InvariantCulture));

    // Round-trip format, normalised to UTC: the same instant must canonicalize identically
    // whichever offset the producing process happened to be running in.
    private static void WriteInstant(ArrayBufferWriter<byte> w, DateTimeOffset value) =>
        WriteText(w, value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture));

    private static void WriteInt64(ArrayBufferWriter<byte> w, long value) =>
        WriteText(w, value.ToString(CultureInfo.InvariantCulture));

    private static void WriteBool(ArrayBufferWriter<byte> w, bool value) =>
        WriteText(w, value ? "1" : "0");

    private static void WriteLength(ArrayBufferWriter<byte> w, int length)
    {
        var span = w.GetSpan(4);
        BinaryPrimitives.WriteInt32BigEndian(span, length);
        w.Advance(4);
    }
}
