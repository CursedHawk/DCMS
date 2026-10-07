using System.Text.Json;
using System.Text.Json.Nodes;
using Dcms.Plugins.DynamicApps.Api.Model;
using Dcms.Plugins.DynamicApps.Automation;
using Dcms.PluginSdk.Abstractions;
using Dcms.PluginSdk.Abstractions.Contracts;
using Dcms.Shared.Audit;
using Dcms.Shared.Data.DynamicApps;
using Dcms.Shared.Data.Rls;
using Dcms.Shared.Security.Authorization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;

namespace Dcms.Plugins.DynamicApps.Metadata;

/// <summary>
/// Which assistant conversation, run and tool call made a change: opaque links, recorded on the
/// revision, its change log and its audit entry, so a configuration change can be traced back
/// to the conversation that asked for it. The transcript is never the configuration record.
/// </summary>
public sealed record AiTrace(Guid? ConversationId, Guid? RunId, string? ToolCallId);

/// <summary>
/// The control plane of one application: its draft, its published revision and their history
/// (ADR 0021). The only code that writes configuration — the admin routes and the assistant's
/// contract call it alike, so a person and the AI are held to the same rules.
///
/// <para><b>Concurrency.</b> Every write locks the app row first, so writes to one application
/// are serial, and names the hash it was made against: a stale one is a
/// <see cref="ContractConflictException"/>, never an overwrite. There is one draft per
/// application; the first change opens it from the published revision.</para>
/// </summary>
public sealed class ConfigurationService(
    AppsDbContext db,
    IPluginContext context,
    IAuditRecorder audit,
    IMemoryCache cache,
    Data.AppEventLog events,
    TimeProvider clock,
    IServiceProvider services)
{
    private const string ResourceType = "app_revision";

    private Guid InstanceId => context.Instance?.InstanceId
        ?? throw new InvalidOperationException("Dynamic Apps configuration is per instance; this context has none.");

    private string? Actor => context.Actor.Id?.ToString() ?? context.Actor.Key;

    // ------------------------------------------------------------------ reads

    public async Task<AppState> GetStateAsync(CancellationToken ct)
    {
        using var rls = RlsScope.Tenant(context.TenantId);
        var app = await db.Apps.AsNoTracking().FirstOrDefaultAsync(a => a.InstanceId == InstanceId, ct);
        if (app is null)
        {
            return new AppState(null, null, ConfigJson.EmptyHash);
        }
        var draft = app.DraftRevisionId is { } d ? await InfoAsync(d, ct) : null;
        var published = app.PublishedRevisionId is { } p ? await InfoAsync(p, ct) : null;
        return new AppState(draft, published, draft?.Hash ?? published?.Hash ?? ConfigJson.EmptyHash);
    }

    public async Task<RevisionDocument?> GetDraftAsync(CancellationToken ct)
    {
        using var rls = RlsScope.Tenant(context.TenantId);
        var app = await db.Apps.AsNoTracking().FirstOrDefaultAsync(a => a.InstanceId == InstanceId, ct);
        return app?.DraftRevisionId is { } id ? await DocumentAsync(id, ct) : null;
    }

    /// <summary>What the runtime serves; null before the first publish. Cached by revision, which never changes.</summary>
    public async Task<RevisionDocument?> GetPublishedAsync(CancellationToken ct)
    {
        using var rls = RlsScope.Tenant(context.TenantId);
        var app = await db.Apps.AsNoTracking().FirstOrDefaultAsync(a => a.InstanceId == InstanceId, ct);
        return app?.PublishedRevisionId is { } id ? await DocumentAsync(id, ct) : null;
    }

    public async Task<RevisionDocument?> GetRevisionAsync(int number, CancellationToken ct)
    {
        using var rls = RlsScope.Tenant(context.TenantId);
        var id = await RevisionIdAsync(number, ct);
        return id is null ? null : await DocumentAsync(id.Value, ct);
    }

    public async Task<RevisionPage> ListRevisionsAsync(int page, int pageSize, CancellationToken ct)
    {
        using var rls = RlsScope.Tenant(context.TenantId);
        pageSize = Math.Clamp(pageSize, 1, 100);
        page = Math.Max(page, 1);
        var revisions = OfThisApp();
        var total = await revisions.CountAsync(ct);
        var items = await revisions.OrderByDescending(r => r.Number)
            .Skip((page - 1) * pageSize).Take(pageSize)
            .Select(WithoutSnapshot).ToListAsync(ct);
        return new RevisionPage(items.Select(Info).ToList(), total);
    }

    /// <summary>The change log of one revision, in the order the changes were made.</summary>
    public async Task<IReadOnlyList<ConfigChange>?> GetChangesAsync(int number, CancellationToken ct)
    {
        using var rls = RlsScope.Tenant(context.TenantId);
        if (await RevisionIdAsync(number, ct) is not { } id)
        {
            return null;
        }
        var rows = await db.Changes.AsNoTracking().Where(c => c.RevisionId == id).OrderBy(c => c.Seq).ToListAsync(ct);
        return rows.Select(c => new ConfigChange
        {
            Op = c.Op,
            ResourceType = c.ResourceType,
            ResourceId = c.ResourceId,
            Path = c.Path,
            Before = c.BeforeJson is null ? null : JsonNode.Parse(c.BeforeJson),
            After = c.AfterJson is null ? null : JsonNode.Parse(c.AfterJson),
        }).ToList();
    }

    public async Task<RevisionDiff?> DiffAsync(int from, int to, CancellationToken ct)
    {
        var a = await GetRevisionAsync(from, ct);
        var b = await GetRevisionAsync(to, ct);
        return a is null || b is null ? null : new RevisionDiff(a.Revision, b.Revision, ConfigDiff.Between(a.Config, b.Config));
    }

    /// <summary>What publishing the draft would change against what is live, and whether it may.</summary>
    public async Task<RevisionPreview> PreviewAsync(CancellationToken ct)
    {
        var draft = await GetDraftAsync(ct) ?? throw NoDraft();
        var published = await GetPublishedAsync(ct);
        return new RevisionPreview(
            draft.Revision,
            published?.Revision,
            ConfigDiff.Between(published?.Config ?? new AppConfig(), draft.Config),
            await IssuesAsync(draft.Config, published?.Config, ct));
    }

    // ------------------------------------------------------------------ writes

    /// <summary>Opens the draft from the published revision; returns the open one if there is one already.</summary>
    public async Task<RevisionInfo> CreateDraftAsync(string? description, AppChangeSource source, CancellationToken ct, AiTrace? ai = null)
    {
        using var rls = RlsScope.Tenant(context.TenantId);
        var (app, tx) = await LockAsync(ct);
        await using var _ = tx;
        if (app.DraftRevisionId is { } open)
        {
            // Nothing happened; recording "revision.created" for it would be a lie.
            if (audit.Declared is { } entry)
            {
                audit.Discard(entry);
            }
            return (await InfoAsync(open, ct))!;
        }
        var draft = await OpenDraftAsync(app, description, source, ct, ai);
        Declare(draft, "revision.created", ai);
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return Info(draft);
    }

    /// <exception cref="ContractConflictException"><paramref name="request"/>'s hash is not the current one.</exception>
    /// <exception cref="ContractValidationException">An operation is malformed or names something that does not exist.</exception>
    public async Task<ApplyChangesResult> ApplyAsync(ApplyChangesRequest request, AppChangeSource source, CancellationToken ct, AiTrace? ai = null)
    {
        using var rls = RlsScope.Tenant(context.TenantId);
        var (app, tx) = await LockAsync(ct);
        await using var _ = tx;

        var current = await CurrentHashAsync(app, ct);
        if (request.ExpectedHash != current)
        {
            throw Stale(current);
        }

        var draft = await OpenDraftAsync(app, request.Description, source, ct, ai);
        var before = ConfigJson.Parse(draft.Snapshot);
        var after = ChangeApplier.Apply(before, request.Operations);
        var changes = ConfigDiff.Between(before, after);

        draft.Snapshot = ConfigJson.Canonical(after);
        draft.Hash = ConfigJson.Hash(after);
        draft.UpdatedAt = clock.GetUtcNow();
        draft.Description ??= request.Description;
        if (source == AppChangeSource.Ai)
        {
            draft.Source = AppChangeSource.Ai;
            draft.SourceConversationId ??= ai?.ConversationId;
            draft.SourceAiRunId ??= ai?.RunId;
        }
        await RecordChangesAsync(draft, changes, source, ct, ai);

        Declare(draft, "change.applied", ai).With("changes", changes.Count);
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);

        var published = await PublishedConfigAsync(app, ct);
        return new ApplyChangesResult(Info(draft), changes, await IssuesAsync(after, published, ct));
    }

    /// <summary>Validates the draft against what is live and, when it passes, stamps it validated.</summary>
    public async Task<ValidationResult> ValidateAsync(CancellationToken ct, AiTrace? ai = null)
    {
        using var rls = RlsScope.Tenant(context.TenantId);
        var (app, tx) = await LockAsync(ct);
        await using var _ = tx;
        var draft = await DraftAsync(app, ct);
        var issues = await IssuesAsync(ConfigJson.Parse(draft.Snapshot), await PublishedConfigAsync(app, ct), ct);
        var result = new ValidationResult(Info(draft), issues);
        if (result.Valid)
        {
            draft.ValidatedAt = clock.GetUtcNow();
            draft.ValidatedHash = draft.Hash;
        }
        Declare(draft, "revision.validated", ai).With("valid", result.Valid).With("issues", issues.Count);
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return result with { Revision = Info(draft) };
    }

    /// <summary>
    /// Makes the draft live: validated, still based on what is live, and the published pointer,
    /// the superseded revision and the audit record moved in one transaction.
    /// </summary>
    public async Task<PublishResult> PublishAsync(string expectedHash, CancellationToken ct, AiTrace? ai = null)
    {
        using var rls = RlsScope.Tenant(context.TenantId);
        var (app, tx) = await LockAsync(ct);
        await using var _ = tx;
        var draft = await DraftAsync(app, ct);
        if (draft.Hash != expectedHash)
        {
            throw Stale(draft.Hash);
        }
        if (draft.BasePublishedId != app.PublishedRevisionId)
        {
            throw new ContractConflictException(
                "The live revision changed after this draft was opened. Discard the draft and open a new one.");
        }

        var config = ConfigJson.Parse(draft.Snapshot);
        var liveConfig = await PublishedConfigAsync(app, ct);
        var issues = (await IssuesAsync(config, liveConfig, ct)).ToList();
        if (!issues.Any(i => i.Severity == IssueSeverity.Error))
        {
            issues.AddRange(await ReconcileDataAsync(liveConfig, config, ct));
        }
        if (issues.Any(i => i.Severity == IssueSeverity.Error))
        {
            Declare(draft, "revision.published", ai).Failed("validation").With("issues", issues.Count);
            return new PublishResult(false, Info(draft), issues);
        }

        var now = clock.GetUtcNow();
        var previous = await SupersedeAsync(app, AppRevisionStatus.Superseded, ct);
        draft.Status = AppRevisionStatus.Published;
        draft.PublishedAt = now;
        draft.PublishedBy = Actor;
        draft.ValidatedAt = now;
        draft.ValidatedHash = draft.Hash;
        app.PublishedRevisionId = draft.Id;
        app.DraftRevisionId = null;
        Published(app, draft, previous);
        await SyncSchedulesAsync(config, ct);

        Declare(draft, "revision.published", ai).With("previous", previous?.Number);
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return new PublishResult(true, Info(draft), issues);
    }

    /// <summary>
    /// Makes an earlier published revision's configuration live again — as a new revision, so
    /// history only ever moves forward and the one rolled back from stays inspectable.
    /// </summary>
    public async Task<PublishResult> RollbackAsync(RollbackRequest request, AppChangeSource source, CancellationToken ct, AiTrace? ai = null)
    {
        using var rls = RlsScope.Tenant(context.TenantId);
        var (app, tx) = await LockAsync(ct);
        await using var _ = tx;
        if (app.DraftRevisionId is not null)
        {
            throw new ContractConflictException("A draft is open. Publish or discard it before rolling back.");
        }
        var live = app.PublishedRevisionId is { } liveId
            ? await db.Revisions.FirstAsync(r => r.Id == liveId, ct)
            : throw new ContractValidationException("Nothing has been published yet, so there is nothing to roll back.");
        if (request.ExpectedPublishedHash is { } expected && expected != live.Hash)
        {
            throw Stale(live.Hash);
        }
        var target = await OfThisApp().AsNoTracking().FirstOrDefaultAsync(r => r.Number == request.ToRevision, ct)
                     ?? throw new ContractValidationException($"There is no revision {request.ToRevision}.");
        if (target.Status is AppRevisionStatus.Draft or AppRevisionStatus.Discarded)
        {
            throw new ContractValidationException($"Revision {target.Number} was never published; only a published revision can be rolled back to.");
        }
        if (target.Id == live.Id)
        {
            throw new ContractValidationException($"Revision {target.Number} is already live.");
        }

        var liveConfig = ConfigJson.Parse(live.Snapshot);
        var config = ConfigJson.Parse(target.Snapshot);
        var issues = (await IssuesAsync(config, liveConfig, ct)).ToList();
        if (!issues.Any(i => i.Severity == IssueSeverity.Error))
        {
            issues.AddRange(await ReconcileDataAsync(liveConfig, config, ct));
        }
        if (issues.Any(i => i.Severity == IssueSeverity.Error))
        {
            audit.Declared?.For(ResourceType, target.Id, $"r{target.Number}").Failed("validation");
            return new PublishResult(false, null, issues);
        }

        var now = clock.GetUtcNow();
        var revision = new AppRevision
        {
            Id = Guid.NewGuid(),
            AppId = app.Id,
            Number = ++app.LastRevisionNumber,
            ParentId = target.Id,
            BasePublishedId = live.Id,
            Status = AppRevisionStatus.Published,
            Source = source,
            Snapshot = ConfigJson.Canonical(config),
            Hash = ConfigJson.Hash(config),
            Description = request.Description ?? $"Rollback to revision {target.Number}",
            CreatedBy = Actor,
            CreatedAt = now,
            UpdatedAt = now,
            ValidatedAt = now,
            PublishedAt = now,
            PublishedBy = Actor,
            SourceConversationId = ai?.ConversationId,
            SourceAiRunId = ai?.RunId,
        };
        revision.ValidatedHash = revision.Hash;
        db.Revisions.Add(revision);
        await SupersedeAsync(app, AppRevisionStatus.RolledBack, ct);
        app.PublishedRevisionId = revision.Id;
        await RecordChangesAsync(revision, ConfigDiff.Between(liveConfig, config), source, ct, ai);
        Published(app, revision, live);
        await SyncSchedulesAsync(config, ct);

        Declare(revision, "revision.rolled_back", ai).With("from", live.Number).With("to", target.Number);
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return new PublishResult(true, Info(revision), issues);
    }

    /// <summary>Throws the draft away. Its revision and change log stay, marked discarded.</summary>
    public async Task DiscardAsync(string expectedHash, CancellationToken ct, AiTrace? ai = null)
    {
        using var rls = RlsScope.Tenant(context.TenantId);
        var (app, tx) = await LockAsync(ct);
        await using var _ = tx;
        var draft = await DraftAsync(app, ct);
        if (draft.Hash != expectedHash)
        {
            throw Stale(draft.Hash);
        }
        draft.Status = AppRevisionStatus.Discarded;
        draft.UpdatedAt = clock.GetUtcNow();
        app.DraftRevisionId = null;
        Declare(draft, "revision.discarded", ai);
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
    }

    // ------------------------------------------------------------------ internals

    /// <summary>The most records a table may hold for a new unique rule to be checked against them at publish.</summary>
    public const int MaxRecordsForNewUnique = 200_000;

    /// <summary>
    /// Brings the records in line with the configuration about to go live, inside the publish
    /// transaction: a new required field must already have a value in every record, and a unique
    /// rule that is new or changed has its keys claimed from the records that exist — a
    /// duplicate among them refuses the publish. Keys of rules that are gone are released.
    /// </summary>
    private async Task<IReadOnlyList<ConfigIssue>> ReconcileDataAsync(AppConfig? live, AppConfig next, CancellationToken ct)
    {
        var issues = new List<ConfigIssue>();
        var instanceId = InstanceId;
        var populated = (await db.Records.Where(r => r.InstanceId == instanceId).Select(r => r.TableId).Distinct().ToListAsync(ct)).ToHashSet();
        var liveTables = (live?.Tables ?? []).Where(t => t.Enabled).ToDictionary(t => t.Id, t => new Data.RuntimeTable(t, live!));
        var nextTables = next.Tables.Where(t => t.Enabled).Select(t => new Data.RuntimeTable(t, next)).ToList();

        // Released: unique rules the next model no longer has.
        var kept = nextTables.SelectMany(t => t.Uniques).Select(u => u.Id).ToHashSet();
        var released = liveTables.Values.SelectMany(t => t.Uniques).Select(u => u.Id).Where(id => !kept.Contains(id)).ToList();
        if (released.Count > 0)
        {
            // Set-based, under the revision.published entry the caller declares.
            await db.UniqueKeys.Where(k => k.InstanceId == instanceId && released.Contains(k.ConstraintId)).ExecuteDeleteAsync(ct);
        }

        foreach (var table in nextTables.Where(t => populated.Contains(t.Id)))
        {
            var was = liveTables.GetValueOrDefault(table.Id);
            var records = db.Records.Where(r => r.InstanceId == instanceId && r.TableId == table.Id);

            foreach (var member in table.Members.Where(m => m.Required && m.Field?.Default is null))
            {
                if (was?.ById.GetValueOrDefault(member.Id) is { Required: true })
                {
                    continue;
                }
                var missing = await records.CountAsync(r => !EF.Functions.JsonExists(r.Data, member.Key), ct);
                if (missing > 0)
                {
                    issues.Add(new ConfigIssue(IssueSeverity.Error, "required-without-data", $"{table.ApiName}.{member.ApiName}",
                        $"{missing} existing record(s) have no value for it. Give it a default, fill it in first, or leave it optional."));
                }
            }

            var liveSignatures = was?.Uniques.ToDictionary(u => u.Id, u => u.Signature) ?? [];
            var rebuild = table.Uniques
                .Where(u => !liveSignatures.TryGetValue(u.Id, out var signature) || signature != u.Signature)
                .Select(u => u.Id).ToHashSet();
            if (rebuild.Count == 0)
            {
                continue;
            }
            if (await records.CountAsync(ct) > MaxRecordsForNewUnique)
            {
                issues.Add(new ConfigIssue(IssueSeverity.Error, "unique-too-large", table.ApiName,
                    $"A new unique rule is checked against every record, and this table has more than {MaxRecordsForNewUnique:N0}."));
                continue;
            }
            await db.UniqueKeys.Where(k => k.InstanceId == instanceId && rebuild.Contains(k.ConstraintId)).ExecuteDeleteAsync(ct);
            var claimed = new Dictionary<(Guid, string), Guid>();
            var clashes = new HashSet<Guid>();
            await foreach (var record in records.AsNoTracking().AsAsyncEnumerable().WithCancellation(ct))
            {
                var data = JsonNode.Parse(record.Data) as JsonObject ?? [];
                foreach (var key in Data.RecordCodec.UniqueKeys(table, data).Where(k => rebuild.Contains(k.ConstraintId)))
                {
                    if (!claimed.TryAdd(key, record.Id))
                    {
                        clashes.Add(key.ConstraintId);
                    }
                }
            }
            foreach (var constraint in table.Uniques.Where(u => clashes.Contains(u.Id)))
            {
                issues.Add(new ConfigIssue(IssueSeverity.Error, "unique-violated-by-data", table.ApiName,
                    $"Existing records share the same {string.Join(" + ", constraint.Members.Select(m => m.ApiName))}; make them distinct before requiring it."));
            }
            if (clashes.Count == 0)
            {
                db.UniqueKeys.AddRange(claimed.Select(c => new AppUniqueKey
                {
                    Id = Guid.NewGuid(), InstanceId = instanceId, ConstraintId = c.Key.Item1, Key = c.Key.Item2, RecordId = c.Value,
                }));
            }
        }
        return issues;
    }

    /// <summary>
    /// The app row, created on first use and locked for the rest of the transaction: every write
    /// to one application queues here, which is what makes "check the hash, then write" safe.
    /// </summary>
    private async Task<(DynamicApp App, IDbContextTransaction Tx)> LockAsync(CancellationToken ct)
    {
        var tenantId = context.TenantId;
        var instanceId = InstanceId;
        // Not through EF: two first writes racing must both succeed, and ON CONFLICT is how.
        await db.Database.ExecuteSqlAsync($"""
            INSERT INTO apps.apps ("Id", "TenantId", "InstanceId", "LastRevisionNumber", "CreatedAt")
            VALUES ({Guid.NewGuid()}, {tenantId}, {instanceId}, 0, now())
            ON CONFLICT ("TenantId", "InstanceId") DO NOTHING
            """, ct);
        var tx = await db.Database.BeginTransactionAsync(ct);
        await db.Database.ExecuteSqlAsync(
            $"""SELECT 1 FROM apps.apps WHERE "TenantId" = {tenantId} AND "InstanceId" = {instanceId} FOR UPDATE""", ct);
        var app = await db.Apps.FirstAsync(a => a.InstanceId == instanceId, ct);
        return (app, tx);
    }

    private async Task<AppRevision> OpenDraftAsync(DynamicApp app, string? description, AppChangeSource source, CancellationToken ct, AiTrace? ai = null)
    {
        if (app.DraftRevisionId is { } open)
        {
            return await db.Revisions.FirstAsync(r => r.Id == open, ct);
        }
        var config = await PublishedConfigAsync(app, ct) ?? new AppConfig();
        var now = clock.GetUtcNow();
        var draft = new AppRevision
        {
            Id = Guid.NewGuid(),
            AppId = app.Id,
            Number = ++app.LastRevisionNumber,
            ParentId = app.PublishedRevisionId,
            BasePublishedId = app.PublishedRevisionId,
            Status = AppRevisionStatus.Draft,
            Source = source,
            Snapshot = ConfigJson.Canonical(config),
            Hash = ConfigJson.Hash(config),
            Description = description,
            CreatedBy = Actor,
            CreatedAt = now,
            UpdatedAt = now,
            SourceConversationId = ai?.ConversationId,
            SourceAiRunId = ai?.RunId,
        };
        db.Revisions.Add(draft);
        app.DraftRevisionId = draft.Id;
        return draft;
    }

    /// <summary>
    /// Makes the schedule rows match the configuration going live, in the publish's transaction.
    /// A schedule whose interval did not change keeps its next due time, so publishing an
    /// unrelated change neither skips nor repeats a tick.
    /// </summary>
    private async Task SyncSchedulesAsync(AppConfig config, CancellationToken ct)
    {
        var instanceId = InstanceId;
        var wanted = config.Flows
            .Where(f => f.Enabled && f.Trigger.Event == "schedule" && f.Trigger.EveryMinutes is > 0)
            .ToDictionary(f => f.Id, f => f.Trigger.EveryMinutes!.Value);
        var existing = await db.FlowSchedules.Where(s => s.InstanceId == instanceId).ToListAsync(ct);
        foreach (var schedule in existing)
        {
            if (!wanted.TryGetValue(schedule.FlowId, out var every))
            {
                db.FlowSchedules.Remove(schedule);
            }
            else if (schedule.EveryMinutes != every)
            {
                schedule.EveryMinutes = every;
                schedule.NextRunAt = clock.GetUtcNow().AddMinutes(every);
            }
        }
        foreach (var (flowId, every) in wanted.Where(w => existing.All(s => s.FlowId != w.Key)))
        {
            db.FlowSchedules.Add(new FlowSchedule
            {
                Id = Guid.NewGuid(), InstanceId = instanceId, FlowId = flowId, EveryMinutes = every,
                NextRunAt = clock.GetUtcNow().AddMinutes(every),
            });
        }
    }

    /// <summary>The <c>revision.published</c> event, in the publish's own transaction.</summary>
    private void Published(DynamicApp app, AppRevision revision, AppRevision? previous) =>
        events.Add(db, revision.Number, Api.AppEvent.RevisionPublished, new Api.AppEventEntity("app", app.Id), new JsonObject
        {
            ["number"] = revision.Number,
            ["hash"] = revision.Hash,
            ["previous"] = previous?.Number,
        });

    private async Task<AppRevision> DraftAsync(DynamicApp app, CancellationToken ct) =>
        app.DraftRevisionId is { } id ? await db.Revisions.FirstAsync(r => r.Id == id, ct) : throw NoDraft();

    private async Task<string> CurrentHashAsync(DynamicApp app, CancellationToken ct)
    {
        var id = app.DraftRevisionId ?? app.PublishedRevisionId;
        return id is null
            ? ConfigJson.EmptyHash
            : await db.Revisions.Where(r => r.Id == id).Select(r => r.Hash).FirstAsync(ct);
    }

    private async Task<AppRevision?> SupersedeAsync(DynamicApp app, AppRevisionStatus status, CancellationToken ct)
    {
        if (app.PublishedRevisionId is not { } id)
        {
            return null;
        }
        var previous = await db.Revisions.FirstAsync(r => r.Id == id, ct);
        previous.Status = status;
        previous.UpdatedAt = clock.GetUtcNow();
        return previous;
    }

    /// <summary>
    /// <see cref="ConfigValidator.Validate"/> against the actions this tenant's flows can use: the
    /// built-in ones, and, once a flow names another, what <c>automation.actions@1</c> providers offer.
    /// </summary>
    public async Task<IReadOnlyList<ConfigIssue>> IssuesAsync(AppConfig config, AppConfig? published, CancellationToken ct)
    {
        if (config.Flows.SelectMany(f => f.Steps).All(s => ActionCatalog.Keys.Contains(s.Action)))
        {
            return ConfigValidator.Validate(config, published);
        }
        var offered = (await ActionCatalog.WithProvidersAsync(context, ct)).ToDictionary(ActionCatalog.Key, StringComparer.Ordinal);
        var issues = ConfigValidator.Validate(config, published, offered.Keys.ToHashSet(StringComparer.Ordinal)).ToList();
        // Another plugin's action can carry that plugin's permission. Whoever changes a flow
        // that uses it, or that can set such a flow off, must hold it — or a flow would let them
        // do what the plugin refuses them by hand. A flow unchanged from the live one was
        // vouched for by whoever published it.
        var needs = ActionCatalog.PermissionsByFlow(config, offered);
        foreach (var flow in config.Flows.Where(f =>
                     published?.Flows.FirstOrDefault(p => p.Id == f.Id) is not { } live || TriggerRouter.Hash(live) != TriggerRouter.Hash(f)))
        {
            foreach (var permission in needs.GetValueOrDefault(flow.ApiName) ?? [])
            {
                if (!await HoldsAsync(permission))
                {
                    issues.Add(new ConfigIssue(IssueSeverity.Error, "action-not-permitted", $"flows.{flow.ApiName}",
                        $"This flow uses, or can start a flow that uses, an action needing the {permission} permission, which you do not have."));
                }
            }
        }
        return issues;
    }

    /// <summary>
    /// Refuses starting a published flow by hand when it uses, or can set off, an action whose
    /// permission the person lacks: a manual flow takes its input from whoever starts it.
    /// </summary>
    /// <exception cref="ContractValidationException">The person lacks one of the permissions.</exception>
    public async Task EnsureMayStartAsync(string flow, CancellationToken ct)
    {
        if (await GetPublishedAsync(ct) is not { } live
            || live.Config.Flows.SelectMany(f => f.Steps).All(s => ActionCatalog.Keys.Contains(s.Action)))
        {
            return;
        }
        var offered = (await ActionCatalog.WithProvidersAsync(context, ct)).ToDictionary(ActionCatalog.Key, StringComparer.Ordinal);
        foreach (var permission in ActionCatalog.PermissionsByFlow(live.Config, offered).GetValueOrDefault(flow) ?? [])
        {
            if (!await HoldsAsync(permission))
            {
                throw new ContractValidationException($"Starting '{flow}' needs the {permission} permission: it uses, or can start a flow that uses, an action that requires it.");
            }
        }
    }

    /// <summary>Whether the member behind this request holds a permission; false outside a request.</summary>
    private async Task<bool> HoldsAsync(string permission)
    {
        var user = services.GetService<IHttpContextAccessor>()?.HttpContext?.User;
        var authz = services.GetService<IAuthorizationService>();
        return user is not null && authz is not null
               && (await authz.AuthorizeAsync(user, null, PermissionPolicyProvider.PolicyName(permission))).Succeeded;
    }

    private async Task<AppConfig?> PublishedConfigAsync(DynamicApp app, CancellationToken ct) =>
        app.PublishedRevisionId is { } id ? (await DocumentAsync(id, ct)).Config : null;

    private async Task RecordChangesAsync(AppRevision revision, IReadOnlyList<ConfigChange> changes, AppChangeSource source, CancellationToken ct,
        AiTrace? ai = null)
    {
        var seq = await db.Changes.Where(c => c.RevisionId == revision.Id).MaxAsync(c => (int?)c.Seq, ct) ?? 0;
        foreach (var change in changes)
        {
            db.Changes.Add(new AppChange
            {
                Id = Guid.NewGuid(),
                RevisionId = revision.Id,
                Seq = ++seq,
                Op = change.Op,
                ResourceType = change.ResourceType,
                ResourceId = change.ResourceId,
                Path = change.Path,
                BeforeJson = change.Before?.ToJsonString(),
                AfterJson = change.After?.ToJsonString(),
                ActorType = source,
                ActorId = Actor,
                AiConversationId = ai?.ConversationId,
                AiRunId = ai?.RunId,
                ToolCallId = ai?.ToolCallId,
                CreatedAt = clock.GetUtcNow(),
            });
        }
    }

    private async Task<RevisionDocument> DocumentAsync(Guid id, CancellationToken ct)
    {
        var revision = await db.Revisions.AsNoTracking().FirstAsync(r => r.Id == id, ct);
        // Published revisions never change, so their parsed form is cached for good; a draft is
        // read fresh every time.
        var config = revision.Status == AppRevisionStatus.Draft
            ? ConfigJson.Parse(revision.Snapshot)
            : await cache.GetOrCreateAsync(("dynamic-apps:revision", revision.Id), entry =>
            {
                entry.SlidingExpiration = TimeSpan.FromMinutes(30);
                return Task.FromResult(ConfigJson.Parse(revision.Snapshot));
            }) ?? ConfigJson.Parse(revision.Snapshot);
        return new RevisionDocument(Info(revision), config);
    }

    private async Task<RevisionInfo?> InfoAsync(Guid id, CancellationToken ct) =>
        await db.Revisions.AsNoTracking().Where(r => r.Id == id).Select(WithoutSnapshot).FirstOrDefaultAsync(ct) is { } r
            ? Info(r)
            : null;

    private async Task<Guid?> RevisionIdAsync(int number, CancellationToken ct) =>
        await OfThisApp().Where(r => r.Number == number).Select(r => (Guid?)r.Id).FirstOrDefaultAsync(ct);

    private IQueryable<AppRevision> OfThisApp()
    {
        var instanceId = InstanceId;
        return db.Revisions.AsNoTracking()
            .Where(r => db.Apps.Any(a => a.Id == r.AppId && a.InstanceId == instanceId));
    }

    /// <summary>Everything but the document, for lists and pointers.</summary>
    private static readonly System.Linq.Expressions.Expression<Func<AppRevision, AppRevision>> WithoutSnapshot = r => new AppRevision
    {
        Id = r.Id,
        TenantId = r.TenantId,
        AppId = r.AppId,
        Number = r.Number,
        ParentId = r.ParentId,
        BasePublishedId = r.BasePublishedId,
        Status = r.Status,
        Source = r.Source,
        Hash = r.Hash,
        SchemaVersion = r.SchemaVersion,
        Description = r.Description,
        CreatedBy = r.CreatedBy,
        CreatedAt = r.CreatedAt,
        UpdatedAt = r.UpdatedAt,
        ValidatedAt = r.ValidatedAt,
        ValidatedHash = r.ValidatedHash,
        PublishedAt = r.PublishedAt,
        PublishedBy = r.PublishedBy,
        SourceConversationId = r.SourceConversationId,
        SourceAiRunId = r.SourceAiRunId,
    };

    private static RevisionInfo Info(AppRevision r) => new()
    {
        Id = r.Id,
        Number = r.Number,
        Status = JsonNamingPolicy.CamelCase.ConvertName(r.Status.ToString()),
        Source = JsonNamingPolicy.CamelCase.ConvertName(r.Source.ToString()),
        Hash = r.Hash,
        ParentId = r.ParentId,
        BasePublishedId = r.BasePublishedId,
        Description = r.Description,
        CreatedBy = r.CreatedBy,
        CreatedAt = r.CreatedAt,
        UpdatedAt = r.UpdatedAt,
        ValidatedAt = r.ValidatedHash == r.Hash ? r.ValidatedAt : null,
        PublishedAt = r.PublishedAt,
        PublishedBy = r.PublishedBy,
        SourceConversationId = r.SourceConversationId,
        SourceAiRunId = r.SourceAiRunId,
    };

    /// <summary>
    /// The record of this write: the route's declared entry (its <c>AuditAs</c>), or — reached
    /// without one, from a job or a contract call — a record of its own under the same name.
    /// </summary>
    private AuditEntry Declare(AppRevision revision, string action, AiTrace? ai = null)
    {
        var entry = (audit.Declared ?? audit.Record($"plugin.{DynamicAppsPlugin.PluginId}.{action}"))
            .For(ResourceType, revision.Id, $"r{revision.Number}")
            .With("instance", InstanceId)
            .With("number", revision.Number)
            .With("hash", revision.Hash);
        if (ai is not null)
        {
            entry.With("ai.conversation", ai.ConversationId).With("ai.run", ai.RunId).With("ai.tool_call", ai.ToolCallId);
        }
        return entry;
    }

    private static ContractConflictException Stale(string current) =>
        new($"The configuration changed since you read it (current hash {current}). Read it again and re-apply your change.");

    private static ContractConflictException NoDraft() =>
        new("There is no open draft. Make a change, or create a draft, first.");
}
