using System.Text.Json;
using System.Text.Json.Nodes;
using Dcms.PluginSdk.Abstractions;
using Dcms.Shared.Data.Rls;
using Dcms.Shared.Data.Tenancy;
using Microsoft.EntityFrameworkCore;

using Dcms.Shared.Data;
namespace Dcms.AdminApi.Environments;

/// <summary>One configuration change of a migration (ADR 0026). A null before is an add, a null after a delete.</summary>
public sealed record ConfigChange(string Kind, string Key, string? Label, JsonElement? Before, JsonElement? After)
{
    public string Op => Before is null ? "add" : After is null ? "delete" : "update";
    public string Id => $"{Kind}/{Key}";
}

/// <summary>What applying one change to the target will do, as the review screen shows it.</summary>
public enum ChangeOutcome
{
    /// <summary>The target holds the change's before: it applies cleanly.</summary>
    Apply,
    /// <summary>The target already holds the change's after: nothing to do.</summary>
    Already,
    /// <summary>The target holds something else: the reviewer chooses overwrite or skip.</summary>
    Conflict,
    /// <summary>The target owns this item itself (<see cref="EnvironmentOwnership.Local"/>): skipped.</summary>
    Local,
}

public sealed record ReviewedChange(ConfigChange Change, ChangeOutcome Outcome, JsonElement? Current);

/// <summary>Who a deployment runs for, captured when it was asked for; checked again when it runs.</summary>
public sealed record EnvironmentApplier(Guid? UserId, bool IsSuperAdmin);

public sealed record PendingMigration(EnvironmentMigration Migration, IReadOnlyList<ReviewedChange> Changes);

/// <summary>
/// Configuration migrations between a workspace's environments (ADR 0026), on EF's model: each
/// environment keeps a baseline of its configuration; <see cref="CutAsync"/> records what changed
/// since as a named migration; <see cref="PendingAsync"/> lists what a source has applied that the
/// target has not, each change reviewed against what the target holds now; <see cref="ApplyAsync"/>
/// applies them in the source's order; <see cref="RollbackAsync"/> reverts the target's last one.
///
/// <para>Every query names its tenant and runs inside that tenant's database scope: this reads and
/// writes environments other than the request's.</para>
/// </summary>
public sealed class EnvironmentMigrator(
    IEnumerable<IEnvironmentConfigKind> kinds, TenancyDbContext db, Dcms.Shared.Security.Authorization.IPermissionResolver permissions)
{
    private readonly IReadOnlyList<IEnvironmentConfigKind> _kinds = kinds.OrderBy(k => k.Order).ToList();

    /// <summary><c>{kind: {key: item}}</c>, in kind registration order.</summary>
    public async Task<Dictionary<string, Dictionary<string, EnvironmentConfigItem>>> ExportAsync(Guid tenantId, CancellationToken ct)
    {
        var snapshot = new Dictionary<string, Dictionary<string, EnvironmentConfigItem>>(StringComparer.Ordinal);
        foreach (var kind in _kinds)
        {
            using var rls = RlsScope.Tenant(tenantId);
            var items = await kind.ExportAsync(tenantId, ct);
            snapshot[kind.Kind] = items.ToDictionary(i => i.Key, StringComparer.Ordinal);
        }
        return snapshot;
    }

    /// <summary>What changed in <paramref name="tenantId"/> since its baseline, leaving out what it owns itself.</summary>
    public async Task<List<ConfigChange>> UnpublishedAsync(Guid tenantId, CancellationToken ct)
    {
        var current = await ExportAsync(tenantId, ct);
        var baseline = await BaselineAsync(tenantId, current, ct);
        var policies = await PoliciesAsync(tenantId, ct);
        return Diff(baseline.Items, current)
            .Where(c => Ownership(policies, c.Kind, c.Key) != EnvironmentOwnership.Local)
            .ToList();
    }

    /// <summary>Records the unpublished changes as a migration and moves the baseline past them; null when there are none.</summary>
    public async Task<EnvironmentMigration?> CutAsync(
        Guid tenantId, string name, string? description, Guid? userId, string? email, CancellationToken ct)
    {
        var current = await ExportAsync(tenantId, ct);
        var baseline = await BaselineAsync(tenantId, current, ct);
        var policies = await PoliciesAsync(tenantId, ct);
        var changes = Diff(baseline.Items, current)
            .Where(c => Ownership(policies, c.Kind, c.Key) != EnvironmentOwnership.Local)
            .ToList();
        if (changes.Count == 0) return null;

        using var rls = RlsScope.Tenant(tenantId);
        var migration = new EnvironmentMigration
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            MigrationId = Guid.NewGuid(),
            OriginTenantId = tenantId,
            Name = name,
            Description = description,
            AuthorUserId = userId,
            AuthorEmail = email,
            ChangesJson = JsonSerializer.Serialize(changes.Select(ToJson)),
            Order = await NextOrderAsync(tenantId, ct),
            AppliedBy = userId,
        };
        db.EnvironmentMigrations.Add(migration);
        foreach (var change in changes) Set(baseline.Items, change.Kind, change.Key, change.After);
        baseline.Row.ItemsJson = Serialize(baseline.Items);
        baseline.Row.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
        return migration;
    }

    /// <summary>
    /// Gives a new environment the source's configuration: every item applied as it is in the
    /// source, the source's history adopted as its own (what it applied is in the copy, so none of
    /// it is pending), and the copy as its baseline.
    /// </summary>
    public async Task CopyAsync(Guid targetId, Guid sourceId, CancellationToken ct)
    {
        var source = await ExportAsync(sourceId, ct);
        foreach (var kind in _kinds)
        {
            foreach (var item in source[kind.Kind].Values.OrderBy(i => i.Key, StringComparer.Ordinal))
            {
                using var scope = RlsScope.Tenant(targetId);
                await kind.ApplyAsync(targetId, item.Key, item.Value, new EnvironmentApplyContext(null), ct);
            }
        }

        List<EnvironmentMigration> history;
        using (RlsScope.Tenant(sourceId))
        {
            history = await db.EnvironmentMigrations.IgnoreTenantFilter().AsNoTracking()
                .Where(m => m.TenantId == sourceId && m.RolledBackAt == null)
                .OrderBy(m => m.Order)
                .ToListAsync(ct);
        }
        using var rls = RlsScope.Tenant(targetId);
        var order = await NextOrderAsync(targetId, ct);
        foreach (var m in history)
        {
            db.EnvironmentMigrations.Add(new EnvironmentMigration
            {
                Id = Guid.NewGuid(), TenantId = targetId, MigrationId = m.MigrationId, OriginTenantId = m.OriginTenantId,
                Name = m.Name, Description = m.Description, AuthorUserId = m.AuthorUserId, AuthorEmail = m.AuthorEmail,
                CreatedAt = m.CreatedAt, ChangesJson = m.ChangesJson, Order = order++, AppliedBy = m.AppliedBy,
            });
        }
        var items = source.ToDictionary(
            k => k.Key, k => k.Value.ToDictionary(i => i.Key, i => i.Value.Value, StringComparer.Ordinal), StringComparer.Ordinal);
        var baseline = await db.EnvironmentBaselines.IgnoreTenantFilter().FirstOrDefaultAsync(b => b.TenantId == targetId, ct);
        if (baseline is null)
        {
            baseline = new EnvironmentBaseline { Id = Guid.NewGuid(), TenantId = targetId };
            db.EnvironmentBaselines.Add(baseline);
        }
        baseline.ItemsJson = Serialize(items);
        baseline.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
    }

    /// <summary>The target's history, newest first.</summary>
    public async Task<List<EnvironmentMigration>> HistoryAsync(Guid tenantId, CancellationToken ct)
    {
        using var rls = RlsScope.Tenant(tenantId);
        return await db.EnvironmentMigrations.IgnoreTenantFilter().AsNoTracking()
            .Where(m => m.TenantId == tenantId)
            .OrderByDescending(m => m.Order)
            .ToListAsync(ct);
    }

    /// <summary>
    /// What <paramref name="sourceId"/> has applied and <paramref name="targetId"/> has not, in the
    /// source's order, each change reviewed against the target as it will be by then: an earlier
    /// pending change to the same item counts as applied.
    /// </summary>
    public async Task<List<PendingMigration>> PendingAsync(Guid targetId, Guid sourceId, CancellationToken ct)
    {
        List<EnvironmentMigration> source;
        using (RlsScope.Tenant(sourceId))
        {
            source = await db.EnvironmentMigrations.IgnoreTenantFilter().AsNoTracking()
                .Where(m => m.TenantId == sourceId && m.RolledBackAt == null)
                .OrderBy(m => m.Order)
                .ToListAsync(ct);
        }
        HashSet<Guid> applied;
        using (RlsScope.Tenant(targetId))
        {
            applied = (await db.EnvironmentMigrations.IgnoreTenantFilter().AsNoTracking()
                .Where(m => m.TenantId == targetId && m.RolledBackAt == null)
                .Select(m => m.MigrationId)
                .ToListAsync(ct)).ToHashSet();
        }

        var current = await ExportAsync(targetId, ct);
        var policies = await PoliciesAsync(targetId, ct);
        var pending = new List<PendingMigration>();
        foreach (var migration in source.Where(m => !applied.Contains(m.MigrationId)))
        {
            var reviewed = Review(Changes(migration), current, policies, forward: true);
            pending.Add(new PendingMigration(migration, reviewed));
            // Later migrations are reviewed against the target as this one will leave it.
            foreach (var r in reviewed.Where(r => r.Outcome is ChangeOutcome.Apply or ChangeOutcome.Conflict))
            {
                SetItem(current, r.Change.Kind, r.Change.Key, r.Change.Label, r.Change.After);
            }
        }
        return pending;
    }

    /// <summary>
    /// Applies the pending migrations from <paramref name="sourceId"/> up to and including
    /// <paramref name="upTo"/>, in order, each recorded in the target's history once all of its
    /// changes are in. A conflict needs a resolution in <paramref name="resolutions"/>
    /// (<c>overwrite</c> or <c>skip</c>, keyed <c>{migrationId}/{changeId}</c>), except in an item the target lets
    /// migrations own, where overwrite is the rule. Every write is authorised for
    /// <paramref name="applier"/> before the first one is made.
    /// </summary>
    /// <returns>How many migrations were applied.</returns>
    public async Task<int> ApplyAsync(
        Guid targetId, Guid sourceId, Guid upTo, IReadOnlyDictionary<string, string> resolutions,
        EnvironmentApplier applier, CancellationToken ct)
    {
        var pending = await PendingAsync(targetId, sourceId, ct);
        var last = pending.FindIndex(p => p.Migration.MigrationId == upTo);
        if (last < 0) throw new EnvironmentMigrationException("environments.errors.migrationNotPending");

        // Reviewed again here, in order, against what the target really holds and will hold: the
        // review screen assumed every conflict overwritten, and a skipped one leaves the item as it
        // was for every later change — to the outcome of those changes and to what they add.
        var policies = await PoliciesAsync(targetId, ct);
        var state = await ExportAsync(targetId, ct);
        var plans = new List<(EnvironmentMigration Migration, List<ReviewedChange> Changes, Dictionary<string, string> Chosen)>();
        foreach (var p in pending.Take(last + 1))
        {
            var reviewed = Review(Changes(p.Migration), state, policies, forward: true);
            var chosen = Choose(p.Migration.MigrationId, reviewed, resolutions, policies);
            foreach (var w in Writes(reviewed, chosen)) SetItem(state, w.Change.Kind, w.Change.Key, w.Change.Label, w.Change.After);
            plans.Add((p.Migration, reviewed, chosen));
        }
        var context = await AuthorizeAsync(targetId, applier, plans.SelectMany(p => Writes(p.Changes, p.Chosen)), ct);

        foreach (var (migration, reviewed, chosen) in plans)
        {
            await WriteAsync(targetId, Writes(reviewed, chosen), context, ct);
            await RecordAsync(targetId, migration, reviewed, chosen, applier.UserId, forward: true, ct);
        }
        return plans.Count;
    }

    /// <summary>Reverts the target's most recent migration, change by change in reverse, the same way.</summary>
    public async Task<EnvironmentMigration> RollbackAsync(
        Guid targetId, Guid migrationId, IReadOnlyDictionary<string, string> resolutions,
        EnvironmentApplier applier, CancellationToken ct)
    {
        var (row, reviewed) = await RollbackPlanAsync(targetId, migrationId, ct);
        // A rollback restores what the migration replaced; no item is the target's to keep here.
        var chosen = Choose(row.MigrationId, reviewed, resolutions, []);
        var context = await AuthorizeAsync(targetId, applier, Writes(reviewed, chosen), ct);
        await WriteAsync(targetId, Writes(reviewed, chosen), context, ct);
        await RecordAsync(targetId, row, reviewed, chosen, applier.UserId, forward: false, ct);
        return row;
    }

    /// <summary>
    /// The reviewer's choice for every conflict, keyed <c>{migrationId}/{changeId}</c> — one item can
    /// change in several pending migrations, and each is decided on its own; overwrite where the
    /// target lets migrations own the item. Returned keyed by change id, as the history records it.
    /// </summary>
    private static Dictionary<string, string> Choose(
        Guid migrationId, IEnumerable<ReviewedChange> reviewed, IReadOnlyDictionary<string, string> resolutions,
        List<EnvironmentPolicy> policies)
    {
        var chosen = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var r in reviewed.Where(r => r.Outcome == ChangeOutcome.Conflict))
        {
            var choice = resolutions.GetValueOrDefault($"{migrationId}/{r.Change.Id}")
                ?? (Ownership(policies, r.Change.Kind, r.Change.Key) == EnvironmentOwnership.Managed ? "overwrite" : null)
                ?? throw new EnvironmentMigrationException("environments.errors.conflictUnresolved", r.Change.Id);
            if (choice is not ("overwrite" or "skip")) throw new EnvironmentMigrationException("environments.errors.conflictUnresolved", r.Change.Id);
            chosen[r.Change.Id] = choice;
        }
        return chosen;
    }

    private static IEnumerable<ReviewedChange> Writes(IEnumerable<ReviewedChange> reviewed, Dictionary<string, string> chosen) =>
        reviewed.Where(r => r.Outcome == ChangeOutcome.Apply
                            || (r.Outcome == ChangeOutcome.Conflict && chosen.GetValueOrDefault(r.Change.Id) == "overwrite"));

    /// <summary>
    /// Refuses the whole deployment unless the applier could make every one of its writes by hand
    /// in the target: each kind's own permission, and for roles every permission a change adds —
    /// the rule that stops a role editor from minting a role beyond their own (SEC-06).
    /// </summary>
    private async Task<EnvironmentApplyContext> AuthorizeAsync(Guid targetId, EnvironmentApplier applier, IEnumerable<ReviewedChange> writes, CancellationToken ct)
    {
        if (applier.IsSuperAdmin) return new EnvironmentApplyContext(null);
        var held = applier.UserId is { } userId
            ? await permissions.GetPermissionsAsync(targetId, userId, ct)
            : new HashSet<string>();
        // What each role really holds as the writes go in: the live export, moved only by them.
        var state = await ExportAsync(targetId, ct);
        foreach (var w in writes)
        {
            var kind = KindOf(w.Change.Kind);
            if (kind.Permission is { } needed && !held.Contains(needed))
            {
                throw new EnvironmentMigrationException("environments.errors.notAllowed", needed);
            }
            if (w.Change.After is { } after)
            {
                using var rls = RlsScope.Tenant(targetId);
                if ((await kind.PermissionsForAsync(targetId, w.Change.Key, after, ct)).FirstOrDefault(p => !held.Contains(p)) is { } lacking)
                {
                    throw new EnvironmentMigrationException("environments.errors.notAllowed", lacking);
                }
            }
            if (w.Change.Kind == "roles")
            {
                JsonElement? now = state.TryGetValue("roles", out var roles) && roles.TryGetValue(w.Change.Key, out var role) ? role.Value : null;
                if (Kinds.RolesConfigKind.Added(now, w.Change.After).FirstOrDefault(p => !held.Contains(p)) is { } beyond)
                {
                    throw new EnvironmentMigrationException("environments.errors.notAllowed", beyond);
                }
            }
            SetItem(state, w.Change.Kind, w.Change.Key, w.Change.Label, w.Change.After);
        }
        return new EnvironmentApplyContext(held);
    }

    private async Task WriteAsync(Guid targetId, IEnumerable<ReviewedChange> writes, EnvironmentApplyContext context, CancellationToken ct)
    {
        foreach (var w in writes)
        {
            using var rls = RlsScope.Tenant(targetId);
            try
            {
                await KindOf(w.Change.Kind).ApplyAsync(targetId, w.Change.Key, w.Change.After, context, ct);
            }
            catch (EnvironmentConfigException ex)
            {
                throw new EnvironmentMigrationException("environments.errors.cannotApply", $"{w.Change.Id}: {ex.Message}");
            }
        }
    }

    /// <summary>What rolling back the target's last migration would do: its changes inverted, reviewed.</summary>
    public async Task<(EnvironmentMigration Row, List<ReviewedChange> Changes)> RollbackPlanAsync(
        Guid targetId, Guid migrationId, CancellationToken ct)
    {
        EnvironmentMigration? row;
        using (RlsScope.Tenant(targetId))
        {
            row = await db.EnvironmentMigrations.IgnoreTenantFilter().AsNoTracking()
                .Where(m => m.TenantId == targetId && m.RolledBackAt == null)
                .OrderByDescending(m => m.Order)
                .FirstOrDefaultAsync(ct);
        }
        if (row is null || row.MigrationId != migrationId)
        {
            throw new EnvironmentMigrationException("environments.errors.rollbackNotLast");
        }
        var inverse = Changes(row).AsEnumerable().Reverse()
            .Select(c => c with { Before = c.After, After = c.Before })
            .ToList();
        var current = await ExportAsync(targetId, ct);
        var policies = await PoliciesAsync(targetId, ct);
        return (row, Review(inverse, current, policies, forward: false));
    }

    private async Task RecordAsync(
        Guid targetId, EnvironmentMigration migration, IReadOnlyList<ReviewedChange> reviewed,
        Dictionary<string, string> chosen, Guid? userId, bool forward, CancellationToken ct)
    {
        using var rls = RlsScope.Tenant(targetId);
        var current = await ExportAsync(targetId, ct);
        var baseline = await BaselineAsync(targetId, current, ct);
        // The baseline follows what the migration wrote, so it is not mistaken for a local edit
        // and cut again here; what was skipped stays a difference, which it is.
        foreach (var r in reviewed)
        {
            var wrote = r.Outcome == ChangeOutcome.Apply
                        || (r.Outcome == ChangeOutcome.Conflict && chosen.GetValueOrDefault(r.Change.Id) == "overwrite")
                        || r.Outcome == ChangeOutcome.Already;
            if (wrote) Set(baseline.Items, r.Change.Kind, r.Change.Key, r.Change.After);
        }
        baseline.Row.ItemsJson = Serialize(baseline.Items);
        baseline.Row.UpdatedAt = DateTimeOffset.UtcNow;

        if (forward)
        {
            db.EnvironmentMigrations.Add(new EnvironmentMigration
            {
                Id = Guid.NewGuid(),
                TenantId = targetId,
                MigrationId = migration.MigrationId,
                OriginTenantId = migration.OriginTenantId,
                Name = migration.Name,
                Description = migration.Description,
                AuthorUserId = migration.AuthorUserId,
                AuthorEmail = migration.AuthorEmail,
                CreatedAt = migration.CreatedAt,
                ChangesJson = migration.ChangesJson,
                Order = await NextOrderAsync(targetId, ct),
                AppliedBy = userId,
                ResolutionsJson = JsonSerializer.Serialize(chosen),
            });
        }
        else
        {
            var row = await db.EnvironmentMigrations.IgnoreTenantFilter()
                .FirstAsync(m => m.TenantId == targetId && m.Id == migration.Id, ct);
            row.RolledBackAt = DateTimeOffset.UtcNow;
        }
        await db.SaveChangesAsync(ct);
    }

    private static List<ReviewedChange> Review(
        IEnumerable<ConfigChange> changes,
        Dictionary<string, Dictionary<string, EnvironmentConfigItem>> current,
        List<EnvironmentPolicy> policies,
        bool forward)
    {
        var reviewed = new List<ReviewedChange>();
        foreach (var change in changes)
        {
            JsonElement? now = current.TryGetValue(change.Kind, out var items) && items.TryGetValue(change.Key, out var item)
                ? item.Value
                : null;
            var outcome = forward && Ownership(policies, change.Kind, change.Key) == EnvironmentOwnership.Local
                ? ChangeOutcome.Local
                : Same(now, change.After) ? ChangeOutcome.Already
                : Same(now, change.Before) ? ChangeOutcome.Apply
                : ChangeOutcome.Conflict;
            reviewed.Add(new ReviewedChange(change, outcome, now));
        }
        return reviewed;
    }

    /// <summary>Every added, changed and removed item between two snapshots, in kind order then key.</summary>
    public static List<ConfigChange> Diff(
        Dictionary<string, Dictionary<string, JsonElement>> from,
        Dictionary<string, Dictionary<string, EnvironmentConfigItem>> to)
    {
        var changes = new List<ConfigChange>();
        foreach (var (kind, items) in to)
        {
            var before = from.GetValueOrDefault(kind) ?? [];
            foreach (var (key, item) in items.OrderBy(i => i.Key, StringComparer.Ordinal))
            {
                JsonElement? old = before.TryGetValue(key, out var b) ? b : null;
                if (!Same(old, item.Value)) changes.Add(new ConfigChange(kind, key, item.Label, old, item.Value));
            }
            foreach (var (key, old) in before.Where(b => !items.ContainsKey(b.Key)).OrderBy(b => b.Key, StringComparer.Ordinal))
            {
                changes.Add(new ConfigChange(kind, key, null, old, null));
            }
        }
        return changes;
    }

    private static bool Same(JsonElement? a, JsonElement? b) =>
        a is null ? b is null : b is not null && JsonElement.DeepEquals(a.Value, b.Value);

    private IEnvironmentConfigKind KindOf(string kind) =>
        _kinds.FirstOrDefault(k => k.Kind == kind)
        ?? throw new EnvironmentMigrationException("environments.errors.unknownKind", kind);

    private sealed record Baseline(EnvironmentBaseline Row, Dictionary<string, Dictionary<string, JsonElement>> Items);

    /// <summary>The tenant's baseline, made from <paramref name="current"/> the first time it is asked for.</summary>
    private async Task<Baseline> BaselineAsync(
        Guid tenantId, Dictionary<string, Dictionary<string, EnvironmentConfigItem>> current, CancellationToken ct)
    {
        using var rls = RlsScope.Tenant(tenantId);
        var row = await db.EnvironmentBaselines.IgnoreTenantFilter().FirstOrDefaultAsync(b => b.TenantId == tenantId, ct);
        if (row is null)
        {
            var items = current.ToDictionary(
                k => k.Key, k => k.Value.ToDictionary(i => i.Key, i => i.Value.Value, StringComparer.Ordinal), StringComparer.Ordinal);
            row = new EnvironmentBaseline { Id = Guid.NewGuid(), TenantId = tenantId, ItemsJson = Serialize(items) };
            db.EnvironmentBaselines.Add(row);
            try
            {
                await db.SaveChangesAsync(ct);
                return new Baseline(row, items);
            }
            catch (DbUpdateException ex) when (ex.InnerException is Npgsql.PostgresException { SqlState: Npgsql.PostgresErrorCodes.UniqueViolation })
            {
                // Made at the same moment elsewhere — the copy job finishing, a second reader: that one stands.
                db.Entry(row).State = EntityState.Detached;
                row = await db.EnvironmentBaselines.IgnoreTenantFilter().FirstAsync(b => b.TenantId == tenantId, ct);
            }
        }
        return new Baseline(row,
            JsonSerializer.Deserialize<Dictionary<string, Dictionary<string, JsonElement>>>(row.ItemsJson) ?? []);
    }

    private async Task<List<EnvironmentPolicy>> PoliciesAsync(Guid tenantId, CancellationToken ct)
    {
        using var rls = RlsScope.Tenant(tenantId);
        return await db.EnvironmentPolicies.IgnoreTenantFilter().AsNoTracking()
            .Where(p => p.TenantId == tenantId)
            .ToListAsync(ct);
    }

    public static EnvironmentOwnership Ownership(IEnumerable<EnvironmentPolicy> policies, string kind, string key) =>
        EnvironmentPolicies.Ownership(policies, kind, key);

    private async Task<long> NextOrderAsync(Guid tenantId, CancellationToken ct) =>
        (await db.EnvironmentMigrations.IgnoreTenantFilter()
            .Where(m => m.TenantId == tenantId)
            .MaxAsync(m => (long?)m.Order, ct) ?? 0) + 1;

    public static List<ConfigChange> Changes(EnvironmentMigration migration) =>
        (JsonNode.Parse(migration.ChangesJson)?.AsArray() ?? [])
        .Select(n => new ConfigChange(
            n!["kind"]!.GetValue<string>(),
            n["key"]!.GetValue<string>(),
            n["label"]?.GetValue<string>(),
            Element(n["before"]),
            Element(n["after"])))
        .ToList();

    private static JsonElement? Element(JsonNode? node) =>
        node is null ? null : JsonSerializer.SerializeToElement(node);

    private static object ToJson(ConfigChange c) =>
        new { kind = c.Kind, key = c.Key, label = c.Label, before = c.Before, after = c.After };

    private static void Set(Dictionary<string, Dictionary<string, JsonElement>> items, string kind, string key, JsonElement? value)
    {
        if (!items.TryGetValue(kind, out var map)) items[kind] = map = new(StringComparer.Ordinal);
        if (value is { } v) map[key] = v;
        else map.Remove(key);
    }

    private static void SetItem(
        Dictionary<string, Dictionary<string, EnvironmentConfigItem>> items, string kind, string key, string? label, JsonElement? value)
    {
        if (!items.TryGetValue(kind, out var map)) items[kind] = map = new(StringComparer.Ordinal);
        if (value is { } v) map[key] = new EnvironmentConfigItem(key, v) { Label = label };
        else map.Remove(key);
    }

    private static string Serialize(Dictionary<string, Dictionary<string, JsonElement>> items) => JsonSerializer.Serialize(items);
}

/// <summary>A migration that cannot go ahead as asked; <see cref="Key"/> is an i18n key.</summary>
public sealed class EnvironmentMigrationException(string key, string? detail = null) : Exception(detail ?? key)
{
    public string Key { get; } = key;
    public string? Detail { get; } = detail;
}
