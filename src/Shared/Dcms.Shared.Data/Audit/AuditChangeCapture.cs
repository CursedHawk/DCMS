using Dcms.Shared.Audit;
using Dcms.Shared.Audit.Redaction;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Metadata;

namespace Dcms.Shared.Data.Audit;

/// <summary>
/// Turns a pending <c>SaveChanges</c> into the "what changed" half of an audit record.
///
/// <para>Runs inside <see cref="AuditOutboxInterceptor"/>, immediately before the buffered
/// entries are enlisted, so the diff commits in the same transaction as the change it
/// describes.</para>
///
/// <para><b>One action, one record.</b> When someone already said what this save is — an
/// endpoint's <c>.WithAudit(...)</c>, or a handler's own entry — every entity touched by it
/// folds into that single record: the matching entity supplies the field diff, and the rest
/// ride along under <c>related</c>. A role update is one record showing the role's new name
/// <i>and</i> the permission rows that came and went, not four rows that each say a table got
/// longer. Only work nobody named — a worker, a consumer, plugin code — produces standalone
/// <c>data.{table}.{op}</c> records, which is the honest thing to say when the action has no
/// name.</para>
/// </summary>
public sealed class AuditChangeCapture(AuditRedactor redactor, IAuditRecorder recorder)
{
    /// <summary>
    /// Above this many touched entities the save is treated as a bulk operation and summarised
    /// by type instead of enumerated. The chain is serialised by a row lock per tenant, so ten
    /// thousand records from one import would not merely be unreadable — they would make the
    /// audit writer the platform's throughput ceiling.
    /// </summary>
    public const int BulkThreshold = 50;

    /// <summary>How many related entities a single record enumerates before it summarises.</summary>
    private const int MaxRelated = 20;

    /// <summary>How many standalone records one undeclared save may produce.</summary>
    private const int MaxStandalone = 10;

    public void Capture(DbContext context)
    {
        // The SavingChanges interceptor fires before EF detects changes, so nothing is
        // Modified yet unless we ask. Idempotent, and the save is about to do it anyway.
        context.ChangeTracker.DetectChanges();

        var changes = Collect(context);
        if (changes.Count == 0)
        {
            return;
        }

        var host = FindHost(changes);

        if (changes.Count > BulkThreshold)
        {
            Summarise(changes, host);
            return;
        }

        if (host is not null)
        {
            Fold(changes, host);
            return;
        }

        Standalone(changes);
    }

    /// <summary>
    /// The entry these changes belong to, if the caller already opened one.
    ///
    /// <para>Usually that is the endpoint's declaration. But code reached from several
    /// directions — <c>SiteDeleter</c>, called both by its own endpoint and by a tenant purge —
    /// records its own entry precisely because there may be no declaration, and a diff that
    /// ignored it would produce two records for one deletion. So: the declaration first, then
    /// any pending entry that names a resource one of these changes is about.</para>
    /// </summary>
    private AuditEntry? FindHost(List<EntityChange> changes)
    {
        if (recorder.Declared is { } declared)
        {
            return declared;
        }

        for (var i = recorder.Pending.Count - 1; i >= 0; i--)
        {
            var candidate = recorder.Pending[i];
            if (candidate.ResourceType is not null && changes.Exists(c => IsAbout(candidate, c)))
            {
                return candidate;
            }
        }

        return null;
    }

    private static bool IsAbout(AuditEntry entry, EntityChange change) =>
        string.Equals(entry.ResourceType, change.ResourceType, StringComparison.OrdinalIgnoreCase)
        && (entry.ResourceId is null || change.ResourceId is null || entry.ResourceId == change.ResourceId);

    /// <summary>Folds every touched entity into the record the endpoint already declared.</summary>
    private static void Fold(List<EntityChange> changes, AuditEntry host)
    {
        // The entity the record is about, if it is among the ones that changed. An id on
        // either side may be absent — a create has none in its route, and a store-generated
        // key is still temporary here — so a matching resource type is enough to pair them.
        var primary = changes.Find(c => IsAbout(host, c));

        if (primary is not null)
        {
            host.ResourceId ??= primary.ResourceId;
            host.ResourceLabel ??= primary.Label;
            (host.Changes ??= []).AddRange(primary.Fields);
            host.RedactionVersion = AuditRedactor.Version;

            if (primary.BeforeUnavailable)
            {
                host.With("diff_before", StubReason);
            }
            changes.Remove(primary);
        }

        if (changes.Count == 0)
        {
            return;
        }

        host.With(
            "related",
            changes.Count <= MaxRelated
                ? changes.Select(c => c.ToRelated()).ToList()
                : changes.GroupBy(c => c.ResourceType, StringComparer.Ordinal)
                    .ToDictionary(g => g.Key, g => (object?)g.Count(), StringComparer.Ordinal));
    }

    /// <summary>Records for changes nobody named — a worker's save, a consumer's, a plugin's.</summary>
    private void Standalone(List<EntityChange> changes)
    {
        foreach (var change in changes.Take(MaxStandalone))
        {
            var entry = recorder.Record(AuditActions.ForChange(change.ResourceType, Verb(change.State)))
                .For(change.ResourceType, change.ResourceId, change.Label);
            entry.Changes = [.. change.Fields];
            entry.RedactionVersion = AuditRedactor.Version;

            if (change.BeforeUnavailable)
            {
                entry.With("diff_before", StubReason);
            }
        }

        if (changes.Count > MaxStandalone)
        {
            recorder.Record(AuditActions.ChangesTruncated)
                .With("truncated", changes.Count - MaxStandalone)
                .With("types", changes.GroupBy(c => c.ResourceType, StringComparer.Ordinal)
                    .ToDictionary(g => g.Key, g => (object?)g.Count(), StringComparer.Ordinal));
        }
    }

    /// <summary>One record with a count manifest, for a save too large to enumerate.</summary>
    private void Summarise(List<EntityChange> changes, AuditEntry? host)
    {
        var manifest = changes
            .GroupBy(c => $"{c.ResourceType}.{Verb(c.State)}", StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => (object?)g.Count(), StringComparer.Ordinal);

        var entry = host ?? recorder.Record(AuditActions.BulkSaved);
        entry.With("bulk", manifest).With("bulk_rows", changes.Count);
    }

    private List<EntityChange> Collect(DbContext context)
    {
        var changes = new List<EntityChange>();

        foreach (var entry in context.ChangeTracker.Entries())
        {
            if (entry.State is not (EntityState.Added or EntityState.Modified or EntityState.Deleted))
            {
                continue;
            }

            var clrType = entry.Metadata.ClrType;
            if (IsExcluded(clrType))
            {
                continue;
            }

            // Past the threshold the save will be summarised by type and count, so building a
            // full diff for each remaining entity would be work thrown away — on exactly the
            // saves that can least afford it.
            changes.Add(changes.Count >= BulkThreshold
                ? new EntityChange(ResourceTypeOf(entry.Metadata, clrType), null, null, entry.State, [], false)
                : Describe(entry, clrType));
        }

        return changes;
    }

    private EntityChange Describe(EntityEntry entry, Type clrType)
    {
        var resourceType = ResourceTypeOf(entry.Metadata, clrType);
        var key = KeyOf(entry);
        var label = LabelOf(entry, clrType);
        var stub = entry.State == EntityState.Modified && IsStubAttach(entry);

        var fields = new List<AuditFieldChange>();

        foreach (var property in entry.Properties)
        {
            if (property.Metadata.IsPrimaryKey() || property.Metadata.Name == nameof(TenantEntity.TenantId))
            {
                // Both are already on the record, as ResourceId and TenantId.
                continue;
            }

            var (before, after) = entry.State switch
            {
                EntityState.Added => (null, property.CurrentValue),
                EntityState.Deleted => (property.OriginalValue, (object?)null),
                _ => (property.OriginalValue, property.CurrentValue),
            };

            if (entry.State == EntityState.Modified)
            {
                if (!property.IsModified)
                {
                    continue;
                }
                if (stub)
                {
                    // The original values on a stub are default(T), not what the row held.
                    before = null;
                }
            }

            if (redactor.Describe(clrType, property.Metadata.Name, before, after) is { } change)
            {
                fields.Add(change);
            }
        }

        return new EntityChange(resourceType, key, label, entry.State, fields, stub);
    }

    /// <summary>
    /// Detects <c>db.Attach(new X { Id = id }); entry.Property(p).IsModified = true</c>, where
    /// every original value is <c>default(T)</c> rather than what the row actually held.
    ///
    /// <para>Left undetected this produces a diff that reads "Name: '' → 'new'" for a row that
    /// was never empty — a confident, specific, wrong answer, which is worse than admitting the
    /// before-values are unknown. The test is that the properties nobody touched are all still
    /// at their defaults, which is true of a stub and false of anything that was loaded.</para>
    /// </summary>
    private static bool IsStubAttach(EntityEntry entry)
    {
        var sawUntouched = false;

        foreach (var property in entry.Properties)
        {
            if (property.IsModified || property.Metadata.IsPrimaryKey())
            {
                continue;
            }

            sawUntouched = true;
            if (property.CurrentValue is not null && !IsClrDefault(property.CurrentValue))
            {
                return false;
            }
        }

        // Everything modified means there is nothing to judge by; assume it was loaded, since
        // treating a real update as a stub silently throws away good before-values.
        return sawUntouched;
    }

    private static bool IsClrDefault(object value) => value switch
    {
        string s => s.Length == 0,
        bool b => !b,
        int i => i == 0,
        long l => l == 0,
        short s => s == 0,
        byte b => b == 0,
        double d => d == 0,
        float f => f == 0,
        decimal d => d == 0,
        Guid g => g == Guid.Empty,
        DateTimeOffset d => d == default,
        DateTime d => d == default,
        Enum e => Convert.ToInt64(e, System.Globalization.CultureInfo.InvariantCulture) == 0,
        _ => false,
    };

    private string ResourceTypeOf(IReadOnlyEntityType entityType, Type clrType)
    {
        // The friendly name when someone has opted the type in, so endpoint records and EF
        // records filter on the same vocabulary; the qualified table name otherwise, which at
        // least tells an investigator where to look.
        if (redactor.ResourceTypeFor(clrType) is { } allowed)
        {
            return allowed;
        }

        var table = entityType.GetTableName();
        var schema = entityType.GetSchema();
        return table is null ? clrType.Name : schema is null ? table : $"{schema}.{table}";
    }

    private static string? KeyOf(EntityEntry entry)
    {
        var key = entry.Metadata.FindPrimaryKey();
        if (key is null)
        {
            return null;
        }

        var parts = key.Properties
            .Select(p => entry.Property(p.Name))
            // A store-generated key is still temporary at this point; claiming a value would be
            // a lie, and the record is still worth having without one.
            .Select(p => p.IsTemporary ? null : p.CurrentValue?.ToString())
            .ToList();

        return parts.Any(p => p is null) ? null : string.Join(":", parts);
    }

    private string? LabelOf(EntityEntry entry, Type clrType)
    {
        if (redactor.LabelPropertyFor(clrType) is not { } name)
        {
            return null;
        }

        var property = entry.Properties.FirstOrDefault(p => p.Metadata.Name == name);
        var value = entry.State == EntityState.Deleted ? property?.OriginalValue : property?.CurrentValue;
        return value?.ToString();
    }

    private static bool IsExcluded(Type clrType) =>
        clrType.GetCustomAttributes(typeof(AuditIgnoreAttribute), inherit: false).Length > 0;

    private static string Verb(EntityState state) => state switch
    {
        EntityState.Added => "created",
        EntityState.Deleted => "deleted",
        _ => "updated",
    };

    private const string StubReason =
        "unavailable: the row was attached as a stub rather than loaded, so its previous values were never read";

    private sealed record EntityChange(
        string ResourceType,
        string? ResourceId,
        string? Label,
        EntityState State,
        IReadOnlyList<AuditFieldChange> Fields,
        bool BeforeUnavailable)
    {
        public Dictionary<string, object?> ToRelated() => new(StringComparer.Ordinal)
        {
            ["type"] = ResourceType,
            ["id"] = ResourceId,
            ["op"] = State switch
            {
                EntityState.Added => "created",
                EntityState.Deleted => "deleted",
                _ => "updated",
            },
            ["changes"] = Fields.Count == 0 ? null : Fields,
        };
    }
}
