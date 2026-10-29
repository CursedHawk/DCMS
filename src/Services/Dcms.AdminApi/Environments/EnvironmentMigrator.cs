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
    /// <summary>
    /// The target changed the item too, elsewhere: the change merges into it field by field
    /// (<see cref="JsonMerge"/>), keeping what the target did.
    /// </summary>
    Merge,
    /// <summary>
    /// Both changed the same field differently (<see cref="ReviewedChange.Conflicts"/>), or one
    /// side added or deleted the item: the reviewer chooses, per path or for the whole change.
    /// </summary>
    Conflict,
    /// <summary>The target owns this item itself (<see cref="EnvironmentOwnership.Local"/>): skipped.</summary>
    Local,
}

/// <param name="Value">
/// What applying writes: the change's after, or the merge of it into <paramref name="Current"/>
/// — for a conflict, with the migration's side taken wherever both changed.
/// </param>
public sealed record ReviewedChange(ConfigChange Change, ChangeOutcome Outcome, JsonElement? Current, JsonElement? Value, IReadOnlyList<MergeConflict> Conflicts);

/// <summary>A change as it will be written, after the reviewer's choices.</summary>
public sealed record ChangeWrite(ConfigChange Change, JsonElement? Value);

/// <summary>Who a deployment runs for, captured when it was asked for; checked again when it runs.</summary>
public sealed record EnvironmentApplier(Guid? UserId, bool IsSuperAdmin);

public sealed record PendingMigration(EnvironmentMigration Migration, IReadOnlyList<ReviewedChange> Changes);

/// <summary>
/// Configuration migrations between a tenant's environments (ADR 0026, on ADR 0028's dimension), on EF's model: each
/// environment keeps a baseline of its configuration; <see cref="CutAsync"/> records what changed
/// since as a named migration; <see cref="PendingAsync"/> lists what a source has applied that the
/// target has not, each change reviewed against what the target holds now; <see cref="ApplyAsync"/>
/// applies them in the source's order; <see cref="RollbackAsync"/> reverts the target's last one.
///
/// <para>Every query names its environment and runs inside that environment's database scope: this
/// reads and writes environments other than the request's. A kind is called inside that scope with
/// the tenant's id, so what it reads and writes stays in the environment.</para>
/// </summary>
public sealed class EnvironmentMigrator(
    IEnumerable<IEnvironmentConfigKind> kinds, TenancyDbContext db, Dcms.Shared.Security.Authorization.IPermissionResolver permissions)
{
    private readonly IReadOnlyList<IEnvironmentConfigKind> _kinds = kinds.OrderBy(k => k.Order).ToList();

    /// <summary><c>{kind: {key: item}}</c>, in kind registration order.</summary>
    public async Task<Dictionary<string, Dictionary<string, EnvironmentConfigItem>>> ExportAsync(TenantEnvironment environment, CancellationToken ct)
    {
        var snapshot = new Dictionary<string, Dictionary<string, EnvironmentConfigItem>>(StringComparer.Ordinal);
        foreach (var kind in _kinds)
        {
            using var rls = environment.Scope();
            var items = await kind.ExportAsync(environment.TenantId, ct);
            snapshot[kind.Kind] = items.ToDictionary(i => i.Key, StringComparer.Ordinal);
        }
        return snapshot;
    }

    /// <summary>What changed in <paramref name="environment"/> since its baseline, leaving out what it owns itself.</summary>
    public async Task<List<ConfigChange>> UnpublishedAsync(TenantEnvironment environment, CancellationToken ct)
    {
        var current = await ExportAsync(environment, ct);
        var baseline = await BaselineAsync(environment, current, ct);
        var policies = await PoliciesAsync(environment, ct);
        return Diff(baseline.Items, current)
            .Where(c => Ownership(policies, c.Kind, c.Key) != EnvironmentOwnership.Local)
            .ToList();
    }

    /// <summary>Records the unpublished changes as a migration and moves the baseline past them; null when there are none.</summary>
    /// <param name="only">
    /// The changes to cut, by id (<c>kind/key</c>); null for all of them. The rest stay
    /// unpublished, for a later migration -- one feature's changes, not everything done since.
    /// </param>
    public async Task<EnvironmentMigration?> CutAsync(
        TenantEnvironment environment, string name, string? description, Guid? userId, string? email, CancellationToken ct,
        IReadOnlyCollection<string>? only = null)
    {
        var current = await ExportAsync(environment, ct);
        var baseline = await BaselineAsync(environment, current, ct);
        var policies = await PoliciesAsync(environment, ct);
        var changes = Diff(baseline.Items, current)
            .Where(c => Ownership(policies, c.Kind, c.Key) != EnvironmentOwnership.Local)
            .Where(c => only is null || only.Contains(c.Id))
            .ToList();
        if (changes.Count == 0) return null;

        using var rls = environment.Scope();
        var migration = new EnvironmentMigration
        {
            Id = Guid.NewGuid(),
            TenantId = environment.TenantId,
            EnvironmentId = environment.Id,
            MigrationId = Guid.NewGuid(),
            OriginEnvironmentId = environment.Id,
            Name = name,
            Description = description,
            AuthorUserId = userId,
            AuthorEmail = email,
            ChangesJson = JsonSerializer.Serialize(changes.Select(ToJson)),
            Order = await NextOrderAsync(environment, ct),
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
    /// <summary>The kinds of configuration, in the order they are applied (and copied).</summary>
    public IEnumerable<string> KindNames => _kinds.Select(k => k.Kind);

    /// <param name="progress">Told (kind, items done, items of the kind, the item about to be copied) as the copy goes.</param>
    public async Task CopyAsync(TenantEnvironment target, TenantEnvironment sourceEnvironment, CancellationToken ct,
        Func<string, int, int, string?, Task>? progress = null)
    {
        var source = await ExportAsync(sourceEnvironment, ct);
        foreach (var kind in _kinds)
        {
            var ofKind = source[kind.Kind].Values.OrderBy(i => i.Key, StringComparer.Ordinal).ToList();
            var done = 0;
            foreach (var item in ofKind)
            {
                if (progress is not null) await progress(kind.Kind, done++, ofKind.Count, item.Label ?? item.Key);
                using var scope = target.Scope();
                try
                {
                    await kind.ApplyAsync(target.TenantId, item.Key, item.Value, new EnvironmentApplyContext(null), ct);
                }
                catch (Exception e) when (e is not OperationCanceledException)
                {
                    // Which item, in words the job can show: the source has something this one refuses.
                    throw new EnvironmentMigrationException("environments.errors.copyRemoved", $"{kind.Kind} '{item.Key}': {e.Message}");
                }
            }
            if (progress is not null) await progress(kind.Kind, ofKind.Count, ofKind.Count, null);
        }

        List<EnvironmentMigration> history;
        using (sourceEnvironment.Scope())
        {
            history = await db.EnvironmentMigrations.IgnoreTenantFilter().AsNoTracking()
                .Where(m => m.EnvironmentId == sourceEnvironment.Id && m.RolledBackAt == null)
                .OrderBy(m => m.Order)
                .ToListAsync(ct);
        }
        using var rls = target.Scope();
        var order = await NextOrderAsync(target, ct);
        foreach (var m in history)
        {
            db.EnvironmentMigrations.Add(new EnvironmentMigration
            {
                Id = Guid.NewGuid(), TenantId = target.TenantId, EnvironmentId = target.Id, MigrationId = m.MigrationId, OriginEnvironmentId = m.OriginEnvironmentId,
                Name = m.Name, Description = m.Description, AuthorUserId = m.AuthorUserId, AuthorEmail = m.AuthorEmail,
                CreatedAt = m.CreatedAt, ChangesJson = m.ChangesJson, Order = order++, AppliedBy = m.AppliedBy,
            });
        }
        // The baseline is the source's configuration, as written here: a site's release is the
        // commit this environment's repository got, not the source's, or every site would read
        // as changed here the moment the copy is done.
        var copied = await ExportAsync(target, ct);
        var items = source.ToDictionary(
            k => k.Key, k => k.Value.ToDictionary(i => i.Key, i => AsWritten(k.Key, i.Key, i.Value.Value, copied)!.Value, StringComparer.Ordinal),
            StringComparer.Ordinal);
        var baseline = await db.EnvironmentBaselines.IgnoreTenantFilter().FirstOrDefaultAsync(b => b.EnvironmentId == target.Id, ct);
        if (baseline is null)
        {
            baseline = new EnvironmentBaseline { Id = Guid.NewGuid(), TenantId = target.TenantId, EnvironmentId = target.Id };
            db.EnvironmentBaselines.Add(baseline);
        }
        baseline.ItemsJson = Serialize(items);
        baseline.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
    }

    /// <summary>The target's history, newest first.</summary>
    public async Task<List<EnvironmentMigration>> HistoryAsync(TenantEnvironment environment, CancellationToken ct)
    {
        using var rls = environment.Scope();
        return await db.EnvironmentMigrations.IgnoreTenantFilter().AsNoTracking()
            .Where(m => m.EnvironmentId == environment.Id)
            .OrderByDescending(m => m.Order)
            .ToListAsync(ct);
    }

    /// <summary>
    /// What <paramref name="source"/> has applied and <paramref name="target"/> has not, in the
    /// source's order, each change reviewed against the target as it will be by then: an earlier
    /// pending change to the same item counts as applied. A source that merges its own changes
    /// only (a feature environment) offers just the migrations it cut, so its target gains exactly
    /// the feature and nothing it inherited.
    /// </summary>
    public async Task<List<PendingMigration>> PendingAsync(TenantEnvironment target, TenantEnvironment source, CancellationToken ct)
    {
        List<EnvironmentMigration> sourceHistory;
        using (source.Scope())
        {
            sourceHistory = await db.EnvironmentMigrations.IgnoreTenantFilter().AsNoTracking()
                .Where(m => m.EnvironmentId == source.Id && m.RolledBackAt == null
                            && (!source.MergesOwnChangesOnly || m.OriginEnvironmentId == source.Id))
                .OrderBy(m => m.Order)
                .ToListAsync(ct);
        }
        HashSet<Guid> applied;
        using (target.Scope())
        {
            applied = (await db.EnvironmentMigrations.IgnoreTenantFilter().AsNoTracking()
                .Where(m => m.EnvironmentId == target.Id && m.RolledBackAt == null)
                .Select(m => m.MigrationId)
                .ToListAsync(ct)).ToHashSet();
        }

        var current = await ExportAsync(target, ct);
        var policies = await PoliciesAsync(target, ct);
        var pending = new List<PendingMigration>();
        foreach (var migration in sourceHistory.Where(m => !applied.Contains(m.MigrationId)))
        {
            var reviewed = Review(Changes(migration), current, policies, forward: true);
            pending.Add(new PendingMigration(migration, reviewed));
            // Later migrations are reviewed against the target as this one will leave it.
            foreach (var r in reviewed.Where(r => r.Outcome is ChangeOutcome.Apply or ChangeOutcome.Merge or ChangeOutcome.Conflict))
            {
                SetItem(current, r.Change.Kind, r.Change.Key, r.Change.Label, r.Value);
            }
        }
        return pending;
    }

    /// <summary>
    /// Applies the pending migrations from <paramref name="source"/> up to and including
    /// <paramref name="upTo"/>, in order, each recorded in the target's history once all of its
    /// changes are in. A conflict needs a resolution in <paramref name="resolutions"/>
    /// (<c>overwrite</c> or <c>skip</c> keyed <c>{migrationId}/{changeId}</c>, or <c>ours</c> or
    /// <c>theirs</c> per conflicting path keyed <c>{migrationId}/{changeId}#{path}</c>), except in an
    /// item the target lets migrations own, where overwrite is the rule. Every write is authorised for
    /// <paramref name="applier"/> before the first one is made.
    /// </summary>
    /// <returns>How many migrations were applied.</returns>
    public async Task<int> ApplyAsync(
        TenantEnvironment target, TenantEnvironment source, Guid upTo, IReadOnlyDictionary<string, string> resolutions,
        EnvironmentApplier applier, CancellationToken ct)
    {
        var pending = await PendingAsync(target, source, ct);
        var last = pending.FindIndex(p => p.Migration.MigrationId == upTo);
        if (last < 0) throw new EnvironmentMigrationException("environments.errors.migrationNotPending");

        // Reviewed again here, in order, against what the target really holds and will hold: the
        // review screen assumed every conflict overwritten, and a skipped one leaves the item as it
        // was for every later change — to the outcome of those changes and to what they add.
        var policies = await PoliciesAsync(target, ct);
        var state = await ExportAsync(target, ct);
        var before = Copy(state);
        var plans = new List<(EnvironmentMigration Migration, List<ReviewedChange> Changes, List<ChangeWrite> Writes, Dictionary<string, string> Chosen)>();
        foreach (var p in pending.Take(last + 1))
        {
            var reviewed = Review(Changes(p.Migration), state, policies, forward: true);
            var (writes, chosen) = Choose(p.Migration.MigrationId, reviewed, resolutions, policies);
            foreach (var w in writes) SetItem(state, w.Change.Kind, w.Change.Key, w.Change.Label, w.Value);
            plans.Add((p.Migration, reviewed, writes, chosen));
        }
        // An item a later migration of this deployment deletes needs none of the writes before it:
        // a site deleted at its source took the commits its earlier changes name with it.
        var deletedAt = plans.SelectMany((p, i) => p.Writes.Where(w => w.Value is null).Select(w => (w.Change.Id, i)))
            .GroupBy(x => x.Id).ToDictionary(g => g.Key, g => g.Max(x => x.i), StringComparer.Ordinal);
        for (var i = 0; i < plans.Count; i++)
        {
            plans[i].Writes.RemoveAll(w => deletedAt.TryGetValue(w.Change.Id, out var at) && at > i);
        }
        var context = await AuthorizeAsync(target, applier, plans.SelectMany(p => p.Writes), ct);

        var started = DateTimeOffset.UtcNow;
        await AllOrNothingAsync(target, before, plans.SelectMany(p => p.Writes), context, async () =>
        {
            foreach (var (migration, reviewed, writes, chosen) in plans)
            {
                await WriteAsync(target, writes, context, ct);
                await RecordAsync(target, migration, reviewed, writes, chosen, applier.UserId, forward: true, ct);
            }
        }, async () =>
        {
            var ids = plans.Select(p => p.Migration.MigrationId).ToList();
            db.EnvironmentMigrations.RemoveRange(await db.EnvironmentMigrations.IgnoreTenantFilter()
                .Where(m => m.EnvironmentId == target.Id && ids.Contains(m.MigrationId) && m.AppliedAt >= started && m.RolledBackAt == null)
                .ToListAsync(CancellationToken.None));
        });
        return plans.Count;
    }

    /// <summary>Reverts the target's most recent migration, change by change in reverse, the same way.</summary>
    public async Task<EnvironmentMigration> RollbackAsync(
        TenantEnvironment target, Guid migrationId, IReadOnlyDictionary<string, string> resolutions,
        EnvironmentApplier applier, CancellationToken ct)
    {
        var (row, reviewed) = await RollbackPlanAsync(target, migrationId, ct);
        var before = await ExportAsync(target, ct);
        // A rollback restores what the migration replaced; no item is the target's to keep here.
        var (writes, chosen) = Choose(row.MigrationId, reviewed, resolutions, []);
        var context = await AuthorizeAsync(target, applier, writes, ct);
        await AllOrNothingAsync(target, before, writes, context, async () =>
        {
            await WriteAsync(target, writes, context, ct);
            await RecordAsync(target, row, reviewed, writes, chosen, applier.UserId, forward: false, ct);
        }, async () =>
        {
            var held = await db.EnvironmentMigrations.IgnoreTenantFilter()
                .FirstAsync(m => m.EnvironmentId == target.Id && m.Id == row.Id, CancellationToken.None);
            held.RolledBackAt = null;
        });
        return row;
    }

    /// <summary>
    /// Runs a deployment's (or a rollback's) writes as all or nothing: when one fails, every item it
    /// touched is put back as it was before it started -- newest kinds first, each through its kind,
    /// as a deployment would write it -- and its history and baseline with them. Git and published
    /// content cannot share a transaction, so this is an undo, not a rollback of one: what could not
    /// be put back is named in the failure.
    /// </summary>
    private async Task AllOrNothingAsync(
        TenantEnvironment target, Dictionary<string, Dictionary<string, EnvironmentConfigItem>> before,
        IEnumerable<ChangeWrite> touched, EnvironmentApplyContext context, Func<Task> work, Func<Task> unrecord)
    {
        string baseline;
        using (target.Scope())
        {
            baseline = (await BaselineAsync(target, before, CancellationToken.None)).Row.ItemsJson;
        }
        var items = touched.Select(w => (w.Change.Kind, w.Change.Key)).Distinct().ToList();
        try
        {
            await work();
        }
        catch (Exception failure)
        {
            var reason = failure is EnvironmentMigrationException m ? m.Detail ?? m.Key : failure.Message;
            db.ChangeTracker.Clear();
            var left = new List<string>();
            var now = await ExportAsync(target, CancellationToken.None);
            var order = _kinds.Select((k, i) => (k.Kind, i)).ToDictionary(x => x.Kind, x => x.i, StringComparer.Ordinal);
            foreach (var (kind, key) in items.OrderByDescending(x => order.GetValueOrDefault(x.Kind)))
            {
                JsonElement? was = before.TryGetValue(kind, out var b) && b.TryGetValue(key, out var bi) ? bi.Value : null;
                JsonElement? @is = now.TryGetValue(kind, out var n) && n.TryGetValue(key, out var ni) ? ni.Value : null;
                if (Same(was, @is)) continue;
                try
                {
                    using var rls = target.Scope();
                    await KindOf(kind).ApplyAsync(target.TenantId, key, was, context with { Before = @is }, CancellationToken.None);
                }
                catch (Exception ex)
                {
                    left.Add($"{kind}/{key}: {ex.Message}");
                }
            }
            try
            {
                using var rls = target.Scope();
                db.ChangeTracker.Clear();
                await unrecord();
                var row = await db.EnvironmentBaselines.IgnoreTenantFilter().FirstAsync(r => r.EnvironmentId == target.Id, CancellationToken.None);
                row.ItemsJson = baseline;
                await db.SaveChangesAsync(CancellationToken.None);
            }
            catch (Exception ex)
            {
                left.Add($"history: {ex.Message}");
            }
            throw left.Count == 0
                ? new EnvironmentMigrationException("environments.errors.undone", reason)
                : new EnvironmentMigrationException("environments.errors.partlyApplied", $"{reason}; not put back: {string.Join("; ", left)}");
        }
    }

    /// <summary>
    /// <paramref name="value"/> with what its kind merged itself taken from what the write left in
    /// the environment (<paramref name="current"/>): git's commit, not the source's.
    /// </summary>
    private JsonElement? AsWritten(string kind, string key, JsonElement? value,
        Dictionary<string, Dictionary<string, EnvironmentConfigItem>> current)
    {
        if (value is not { ValueKind: JsonValueKind.Object } || !current.TryGetValue(kind, out var now)
            || !now.TryGetValue(key, out var written) || written.Value.ValueKind != JsonValueKind.Object)
        {
            return value;
        }
        var properties = KindOf(kind).SelfMergedProperties;
        if (properties.Count == 0) return value;
        var node = JsonNode.Parse(value.Value.GetRawText())!.AsObject();
        foreach (var property in properties)
        {
            node[property] = written.Value.TryGetProperty(property, out var p) ? JsonNode.Parse(p.GetRawText()) : null;
        }
        return JsonSerializer.SerializeToElement(node);
    }

    private static Dictionary<string, Dictionary<string, EnvironmentConfigItem>> Copy(
        Dictionary<string, Dictionary<string, EnvironmentConfigItem>> items) =>
        items.ToDictionary(k => k.Key, k => new Dictionary<string, EnvironmentConfigItem>(k.Value, StringComparer.Ordinal), StringComparer.Ordinal);

    /// <summary>
    /// What each change writes once the reviewer's choices are in: a whole-change choice keyed
    /// <c>{migrationId}/{changeId}</c> (<c>overwrite</c> or <c>skip</c>), or one per conflicting path
    /// keyed <c>{migrationId}/{changeId}#{path}</c> (<c>ours</c> or <c>theirs</c>) — one item can
    /// change in several pending migrations, and each is decided on its own. Overwrite where the
    /// target lets migrations own the item. The choices come back keyed by change id (and path),
    /// as the history records them.
    /// </summary>
    private (List<ChangeWrite> Writes, Dictionary<string, string> Chosen) Choose(
        Guid migrationId, IEnumerable<ReviewedChange> reviewed, IReadOnlyDictionary<string, string> resolutions,
        List<EnvironmentPolicy> policies)
    {
        var writes = new List<ChangeWrite>();
        var chosen = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var r in reviewed)
        {
            if (r.Outcome is ChangeOutcome.Apply or ChangeOutcome.Merge)
            {
                // Any change may be left out here by the reviewer: a site whose code clashes only
                // shows when git lays it on, and need not hold back everything after it.
                if (resolutions.GetValueOrDefault($"{migrationId}/{r.Change.Id}") == "skip") chosen[r.Change.Id] = "skip";
                else writes.Add(new ChangeWrite(r.Change, r.Value));
                continue;
            }
            if (r.Outcome != ChangeOutcome.Conflict) continue;

            var whole = resolutions.GetValueOrDefault($"{migrationId}/{r.Change.Id}")
                ?? (Ownership(policies, r.Change.Kind, r.Change.Key) == EnvironmentOwnership.Managed ? "overwrite" : null);
            if (whole is not null)
            {
                if (whole is not ("overwrite" or "skip")) throw new EnvironmentMigrationException("environments.errors.conflictUnresolved", r.Change.Id);
                chosen[r.Change.Id] = whole;
                if (whole == "overwrite") writes.Add(new ChangeWrite(r.Change, r.Value));
                continue;
            }
            // The item as a whole (added on both sides, deleted on one) has no paths to choose between.
            if (r.Conflicts.Any(c => c.Path.Length == 0)) throw new EnvironmentMigrationException("environments.errors.conflictUnresolved", r.Change.Id);
            var paths = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var conflict in r.Conflicts)
            {
                var choice = resolutions.GetValueOrDefault($"{migrationId}/{r.Change.Id}#{conflict.Path}");
                if (choice is not ("ours" or "theirs")) throw new EnvironmentMigrationException("environments.errors.conflictUnresolved", $"{r.Change.Id}#{conflict.Path}");
                paths[conflict.Path] = choice;
                chosen[$"{r.Change.Id}#{conflict.Path}"] = choice;
            }
            var own = SelfMerged(r.Change.Kind);
            var value = MergedValue(r.Change, r.Current, path => paths.GetValueOrDefault(path) ?? own(path));
            if (!Same(value, r.Current)) writes.Add(new ChangeWrite(r.Change, value));
        }
        return (writes, chosen);
    }

    /// <summary>The change merged into <paramref name="current"/>, conflicts settled by <paramref name="choose"/>.</summary>
    private static JsonElement? MergedValue(ConfigChange change, JsonElement? current, Func<string, string?> choose)
    {
        var (merged, _) = JsonMerge.ThreeWay(Node(change.Before), Node(current), Node(change.After), choose);
        return merged is null ? null : JsonSerializer.SerializeToElement(merged);
    }

    private static JsonNode? Node(JsonElement? element) => element is { } e ? JsonNode.Parse(e.GetRawText()) : null;

    /// <summary>"theirs" for a top-level property the kind merges itself, else no say.</summary>
    private Func<string, string?> SelfMerged(string kind)
    {
        var properties = _kinds.FirstOrDefault(k => k.Kind == kind)?.SelfMergedProperties ?? [];
        return path => properties.Any(p => path == "/" + p) ? "theirs" : null;
    }

    /// <summary>
    /// Refuses the whole deployment unless the applier could make every one of its writes by hand
    /// in the target: each kind's own permission, and for roles every permission a change adds —
    /// the rule that stops a role editor from minting a role beyond their own (SEC-06).
    /// </summary>
    private async Task<EnvironmentApplyContext> AuthorizeAsync(TenantEnvironment target, EnvironmentApplier applier, IEnumerable<ChangeWrite> writes, CancellationToken ct)
    {
        if (applier.IsSuperAdmin) return new EnvironmentApplyContext(null);
        IReadOnlySet<string> held = new HashSet<string>();
        if (applier.UserId is { } userId)
        {
            using var scope = target.Scope();
            held = await permissions.GetPermissionsAsync(target.TenantId, userId, ct);
        }
        // What each role really holds as the writes go in: the live export, moved only by them.
        var state = await ExportAsync(target, ct);
        foreach (var w in writes)
        {
            var kind = KindOf(w.Change.Kind);
            if (kind.Permission is { } needed && !held.Contains(needed))
            {
                throw new EnvironmentMigrationException("environments.errors.notAllowed", needed);
            }
            if (w.Value is { } after)
            {
                using var rls = target.Scope();
                if ((await kind.PermissionsForAsync(target.TenantId, w.Change.Key, after, ct)).FirstOrDefault(p => !held.Contains(p)) is { } lacking)
                {
                    throw new EnvironmentMigrationException("environments.errors.notAllowed", lacking);
                }
            }
            if (w.Change.Kind == "roles")
            {
                JsonElement? now = state.TryGetValue("roles", out var roles) && roles.TryGetValue(w.Change.Key, out var role) ? role.Value : null;
                if (Kinds.RolesConfigKind.Added(now, w.Value).FirstOrDefault(p => !held.Contains(p)) is { } beyond)
                {
                    throw new EnvironmentMigrationException("environments.errors.notAllowed", beyond);
                }
            }
            SetItem(state, w.Change.Kind, w.Change.Key, w.Change.Label, w.Value);
        }
        return new EnvironmentApplyContext(held) { UserId = applier.UserId };
    }

    private async Task WriteAsync(TenantEnvironment target, IEnumerable<ChangeWrite> writes, EnvironmentApplyContext context, CancellationToken ct)
    {
        foreach (var w in writes)
        {
            using var rls = target.Scope();
            try
            {
                await KindOf(w.Change.Kind).ApplyAsync(target.TenantId, w.Change.Key, w.Value, context with { Before = w.Change.Before }, ct);
            }
            catch (EnvironmentConfigException ex)
            {
                throw new EnvironmentMigrationException("environments.errors.cannotApply", $"{w.Change.Id}: {ex.Message}");
            }
        }
    }

    /// <summary>What rolling back the target's last migration would do: its changes inverted, reviewed.</summary>
    public async Task<(EnvironmentMigration Row, List<ReviewedChange> Changes)> RollbackPlanAsync(
        TenantEnvironment target, Guid migrationId, CancellationToken ct)
    {
        EnvironmentMigration? row;
        using (target.Scope())
        {
            row = await db.EnvironmentMigrations.IgnoreTenantFilter().AsNoTracking()
                .Where(m => m.EnvironmentId == target.Id && m.RolledBackAt == null)
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
        var current = await ExportAsync(target, ct);
        var policies = await PoliciesAsync(target, ct);
        return (row, Review(inverse, current, policies, forward: false));
    }

    private async Task RecordAsync(
        TenantEnvironment target, EnvironmentMigration migration, IReadOnlyList<ReviewedChange> reviewed, IReadOnlyList<ChangeWrite> writes,
        Dictionary<string, string> chosen, Guid? userId, bool forward, CancellationToken ct)
    {
        using var rls = target.Scope();
        var current = await ExportAsync(target, ct);
        var baseline = await BaselineAsync(target, current, ct);
        // The baseline takes what the migration changed, so that is not mistaken for a local edit
        // and cut again here. Only the migration's own change, merged into the baseline: the
        // target's edits not yet cut stay a difference, as does whatever was skipped or kept.
        var wrote = writes.Select(w => w.Change.Id).ToHashSet(StringComparer.Ordinal);
        foreach (var r in reviewed.Where(r => r.Outcome == ChangeOutcome.Already || wrote.Contains(r.Change.Id)))
        {
            JsonElement? held = baseline.Items.TryGetValue(r.Change.Kind, out var items) && items.TryGetValue(r.Change.Key, out var b) ? b : null;
            var value = r.Change.Before is null || r.Change.After is null || held is null ? r.Change.After : MergedValue(r.Change, held, _ => "theirs");
            Set(baseline.Items, r.Change.Kind, r.Change.Key, AsWritten(r.Change.Kind, r.Change.Key, value, current));
        }
        baseline.Row.ItemsJson = Serialize(baseline.Items);
        baseline.Row.UpdatedAt = DateTimeOffset.UtcNow;

        if (forward)
        {
            db.EnvironmentMigrations.Add(new EnvironmentMigration
            {
                Id = Guid.NewGuid(),
                TenantId = target.TenantId,
                EnvironmentId = target.Id,
                MigrationId = migration.MigrationId,
                OriginEnvironmentId = migration.OriginEnvironmentId,
                Name = migration.Name,
                Description = migration.Description,
                AuthorUserId = migration.AuthorUserId,
                AuthorEmail = migration.AuthorEmail,
                CreatedAt = migration.CreatedAt,
                // What this environment took: a change the reviewer left out here is not passed on
                // from here either -- the next stage is offered what this one has.
                ChangesJson = JsonSerializer.Serialize(Changes(migration).Where(c => chosen.GetValueOrDefault(c.Id) != "skip").Select(ToJson)),
                Order = await NextOrderAsync(target, ct),
                AppliedBy = userId,
                ResolutionsJson = JsonSerializer.Serialize(chosen),
            });
        }
        else
        {
            var row = await db.EnvironmentMigrations.IgnoreTenantFilter()
                .FirstAsync(m => m.EnvironmentId == target.Id && m.Id == migration.Id, ct);
            row.RolledBackAt = DateTimeOffset.UtcNow;
        }
        await db.SaveChangesAsync(ct);
    }

    private List<ReviewedChange> Review(
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
            if (forward && Ownership(policies, change.Kind, change.Key) == EnvironmentOwnership.Local)
            {
                reviewed.Add(new ReviewedChange(change, ChangeOutcome.Local, now, now, []));
            }
            else if (Same(now, change.After))
            {
                reviewed.Add(new ReviewedChange(change, ChangeOutcome.Already, now, now, []));
            }
            else if (Same(now, change.Before))
            {
                reviewed.Add(new ReviewedChange(change, ChangeOutcome.Apply, now, change.After, []));
            }
            else if (now is null || change.Before is null || change.After is null)
            {
                // Added on both sides, or deleted on one side and changed on the other: the whole item.
                reviewed.Add(new ReviewedChange(change, ChangeOutcome.Conflict, now, change.After,
                    [new MergeConflict("", Node(now), Node(change.After))]));
            }
            else
            {
                // What the kind merges itself (a site's code, by git) is the migration's to bring.
                var own = SelfMerged(change.Kind);
                var (_, conflicts) = JsonMerge.ThreeWay(Node(change.Before), Node(now), Node(change.After), own);
                reviewed.Add(new ReviewedChange(change, conflicts.Count == 0 ? ChangeOutcome.Merge : ChangeOutcome.Conflict,
                    now, MergedValue(change, now, _ => "theirs"), conflicts));
            }
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

    /// <summary>The environment's baseline, made from <paramref name="current"/> the first time it is asked for.</summary>
    private async Task<Baseline> BaselineAsync(
        TenantEnvironment environment, Dictionary<string, Dictionary<string, EnvironmentConfigItem>> current, CancellationToken ct)
    {
        using var rls = environment.Scope();
        var row = await db.EnvironmentBaselines.IgnoreTenantFilter().FirstOrDefaultAsync(b => b.EnvironmentId == environment.Id, ct);
        if (row is null)
        {
            var items = current.ToDictionary(
                k => k.Key, k => k.Value.ToDictionary(i => i.Key, i => i.Value.Value, StringComparer.Ordinal), StringComparer.Ordinal);
            row = new EnvironmentBaseline { Id = Guid.NewGuid(), TenantId = environment.TenantId, EnvironmentId = environment.Id, ItemsJson = Serialize(items) };
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
                row = await db.EnvironmentBaselines.IgnoreTenantFilter().FirstAsync(b => b.EnvironmentId == environment.Id, ct);
            }
        }
        var held = JsonSerializer.Deserialize<Dictionary<string, Dictionary<string, JsonElement>>>(row.ItemsJson) ?? [];
        // A kind added since the baseline was made starts from what the environment holds now:
        // otherwise everything of it would show as unpublished in every environment at once.
        var added = current.Keys.Where(k => !held.ContainsKey(k)).ToList();
        if (added.Count > 0)
        {
            foreach (var kind in added)
            {
                held[kind] = current[kind].ToDictionary(i => i.Key, i => i.Value.Value, StringComparer.Ordinal);
            }
            row.ItemsJson = Serialize(held);
            await db.SaveChangesAsync(ct);
        }
        return new Baseline(row, held);
    }

    private async Task<List<EnvironmentPolicy>> PoliciesAsync(TenantEnvironment environment, CancellationToken ct)
    {
        using var rls = environment.Scope();
        return await db.EnvironmentPolicies.IgnoreTenantFilter().AsNoTracking()
            .Where(p => p.EnvironmentId == environment.Id)
            .ToListAsync(ct);
    }

    public static EnvironmentOwnership Ownership(IEnumerable<EnvironmentPolicy> policies, string kind, string key) =>
        EnvironmentPolicies.Ownership(policies, kind, key);

    private async Task<long> NextOrderAsync(TenantEnvironment environment, CancellationToken ct) =>
        (await db.EnvironmentMigrations.IgnoreTenantFilter()
            .Where(m => m.EnvironmentId == environment.Id)
            .MaxAsync(m => (long?)m.Order, ct) ?? 0) + 1;

    /// <summary>
    /// What the migration changed where this row was recorded: a change the reviewer left out there
    /// is not among them (rows recorded before that was dropped on recording still name it).
    /// </summary>
    public static List<ConfigChange> Changes(EnvironmentMigration migration)
    {
        var skipped = migration.ResolutionsJson is { Length: > 0 } resolutions
            ? (JsonSerializer.Deserialize<Dictionary<string, string>>(resolutions) ?? [])
                .Where(r => r.Value == "skip").Select(r => r.Key).ToHashSet(StringComparer.Ordinal)
            : [];
        return (JsonNode.Parse(migration.ChangesJson)?.AsArray() ?? [])
            .Select(n => new ConfigChange(
                n!["kind"]!.GetValue<string>(),
                n["key"]!.GetValue<string>(),
                n["label"]?.GetValue<string>(),
                Element(n["before"]),
                Element(n["after"])))
            .Where(c => !skipped.Contains(c.Id))
            .ToList();
    }

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
