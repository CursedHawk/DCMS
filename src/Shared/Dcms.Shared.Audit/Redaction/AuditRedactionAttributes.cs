namespace Dcms.Shared.Audit.Redaction;

/// <summary>
/// The property's values must never appear in a record. The field still shows up in the diff
/// as <c>Redacted = true</c>, because "this secret changed" and "this field did not change"
/// are different facts and an investigator needs to tell them apart.
/// </summary>
[AttributeUsage(AttributeTargets.Property)]
public sealed class AuditSensitiveAttribute : Attribute;

/// <summary>
/// Not worth recording at all: a cached counter, a denormalised copy, a column the
/// application rewrites on every save. Omitted from the diff entirely, as opposed to
/// <see cref="AuditSensitiveAttribute"/>, which records that it changed.
///
/// <para>On a whole entity type it means the table's rows are never an action in their own
/// right — a telemetry sink, a derived index, an outbox. Those tables are written on paths
/// that are themselves audited, and recording each row would bury the actions under the
/// mechanics of carrying them out.</para>
/// </summary>
[AttributeUsage(AttributeTargets.Property | AttributeTargets.Class)]
public sealed class AuditIgnoreAttribute : Attribute;

/// <summary>
/// Opts an entity type into full value capture and names the resource type its records
/// carry. Without it a type is default-deny: changes are recorded as field <i>names</i> only.
/// See <see cref="AuditRedactor"/> for why that default is the right way round.
/// </summary>
/// <param name="resourceType">
/// The value that lands in <c>ResourceType</c> — the vocabulary the read plane filters on,
/// so it should match what <c>.WithAudit(..., resourceType)</c> uses at the endpoints
/// ("site", "content_item", "media_asset").
/// </param>
[AttributeUsage(AttributeTargets.Class)]
public sealed class AuditedAttribute(string resourceType) : Attribute
{
    public string ResourceType { get; } = resourceType;

    /// <summary>Property whose value makes a human-readable label for the row.</summary>
    public string? LabelProperty { get; init; }
}
