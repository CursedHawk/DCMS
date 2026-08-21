namespace Dcms.Shared.Audit;

/// <summary>
/// A record being composed. Callers state the *meaning* — what happened, to what — and the
/// recorder supplies the ambient envelope (actor, tenant, request, correlation, provenance)
/// when the buffer is flushed.
///
/// Mutable on purpose: the three capture layers contribute to the same entry. An endpoint
/// states the action, the EF interceptor attaches the field diff, and the middleware stamps
/// the status code once the response is known.
/// </summary>
public sealed class AuditEntry
{
    /// <summary>Stable id, assigned up front so a caller can link a later entry to this one.</summary>
    public Guid EventId { get; } = Guid.CreateVersion7();

    public required string Action { get; set; }

    public AuditCategory Category { get; set; } = AuditCategory.TenantState;
    public AuditOutcome Outcome { get; set; } = AuditOutcome.Success;
    public AuditSeverity Severity { get; set; } = AuditSeverity.Info;

    /// <summary>
    /// Overrides the ambient tenant. Required on cross-tenant paths — the git webhook and
    /// every worker, none of which have a Finbuckle tenant context. <see cref="Guid.Empty"/>
    /// is the explicit platform scope, not "unset"; leave this null to inherit the scope.
    /// </summary>
    public Guid? TenantId { get; set; }

    /// <summary>The user this event is about, when that is not the actor.</summary>
    public Guid? SubjectUserId { get; set; }

    public string? ResourceType { get; set; }
    public string? ResourceId { get; set; }
    public string? ResourceLabel { get; set; }

    public List<AuditFieldChange>? Changes { get; set; }
    public Dictionary<string, object?>? Metadata { get; set; }

    /// <summary>Defaults to record time; set explicitly when the action happened earlier.</summary>
    public DateTimeOffset? OccurredAt { get; set; }

    public Guid? CausationId { get; set; }

    public int RedactionVersion { get; set; }
}

/// <summary>
/// Terse composition helpers, so recording stays a single expression at the call site and
/// does not visually swamp the business logic it accompanies.
/// </summary>
public static class AuditEntryExtensions
{
    public static AuditEntry For(this AuditEntry entry, string resourceType, object? resourceId, string? label = null)
    {
        entry.ResourceType = resourceType;
        entry.ResourceId = resourceId?.ToString();
        entry.ResourceLabel = label;
        return entry;
    }

    public static AuditEntry With(this AuditEntry entry, string key, object? value)
    {
        (entry.Metadata ??= [])[key] = value;
        return entry;
    }

    public static AuditEntry Changed(this AuditEntry entry, string field, object? before, object? after)
    {
        (entry.Changes ??= []).Add(new AuditFieldChange(field, before, after, Redacted: false));
        return entry;
    }

    /// <summary>Records that a field changed without disclosing either value.</summary>
    public static AuditEntry ChangedSecret(this AuditEntry entry, string field)
    {
        (entry.Changes ??= []).Add(new AuditFieldChange(field, null, null, Redacted: true));
        return entry;
    }

    public static AuditEntry InTenant(this AuditEntry entry, Guid tenantId)
    {
        entry.TenantId = tenantId;
        return entry;
    }

    /// <summary>Belongs to no tenant — a login, or a tenant being provisioned.</summary>
    public static AuditEntry Platform(this AuditEntry entry)
    {
        entry.TenantId = Guid.Empty;
        return entry;
    }

    public static AuditEntry About(this AuditEntry entry, Guid subjectUserId)
    {
        entry.SubjectUserId = subjectUserId;
        return entry;
    }

    public static AuditEntry Failed(this AuditEntry entry, string? reason = null)
    {
        entry.Outcome = AuditOutcome.Failure;
        entry.Severity = AuditSeverity.Warning;
        return reason is null ? entry : entry.With("reason", reason);
    }

    public static AuditEntry Denied(this AuditEntry entry, string? reason = null)
    {
        entry.Outcome = AuditOutcome.Denied;
        entry.Category = AuditCategory.Security;
        entry.Severity = AuditSeverity.Notice;
        return reason is null ? entry : entry.With("reason", reason);
    }

    public static AuditEntry As(this AuditEntry entry, AuditCategory category, AuditSeverity? severity = null)
    {
        entry.Category = category;
        if (severity is not null)
        {
            entry.Severity = severity.Value;
        }
        return entry;
    }

    public static AuditEntry CausedBy(this AuditEntry entry, Guid causationId)
    {
        entry.CausationId = causationId;
        return entry;
    }
}
