using System.Text.Json;
using System.Text.Json.Nodes;
using Dcms.Plugins.DynamicApps.Api.Model;
using Dcms.PluginSdk.Abstractions;
using Dcms.PluginSdk.Abstractions.Contracts;
using Dcms.Shared.Audit;
using Dcms.Shared.Data.DynamicApps;
using Dcms.Shared.Data.Rls;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Caching.Memory;

namespace Dcms.Plugins.DynamicApps.Metadata;

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
    TimeProvider clock)
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
            ConfigValidator.Validate(draft.Config, published?.Config));
    }

    // ------------------------------------------------------------------ writes

    /// <summary>Opens the draft from the published revision; returns the open one if there is one already.</summary>
    public async Task<RevisionInfo> CreateDraftAsync(string? description, AppChangeSource source, CancellationToken ct)
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
        var draft = await OpenDraftAsync(app, description, source, ct);
        Declare(draft, "revision.created");
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return Info(draft);
    }

    /// <exception cref="ContractConflictException"><paramref name="request"/>'s hash is not the current one.</exception>
    /// <exception cref="ContractValidationException">An operation is malformed or names something that does not exist.</exception>
    public async Task<ApplyChangesResult> ApplyAsync(ApplyChangesRequest request, AppChangeSource source, CancellationToken ct)
    {
        using var rls = RlsScope.Tenant(context.TenantId);
        var (app, tx) = await LockAsync(ct);
        await using var _ = tx;

        var current = await CurrentHashAsync(app, ct);
        if (request.ExpectedHash != current)
        {
            throw Stale(current);
        }

        var draft = await OpenDraftAsync(app, request.Description, source, ct);
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
        }
        await RecordChangesAsync(draft, changes, source, ct);

        Declare(draft, "change.applied").With("changes", changes.Count);
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);

        var published = await PublishedConfigAsync(app, ct);
        return new ApplyChangesResult(Info(draft), changes, ConfigValidator.Validate(after, published));
    }

    /// <summary>Validates the draft against what is live and, when it passes, stamps it validated.</summary>
    public async Task<ValidationResult> ValidateAsync(CancellationToken ct)
    {
        using var rls = RlsScope.Tenant(context.TenantId);
        var (app, tx) = await LockAsync(ct);
        await using var _ = tx;
        var draft = await DraftAsync(app, ct);
        var issues = ConfigValidator.Validate(ConfigJson.Parse(draft.Snapshot), await PublishedConfigAsync(app, ct));
        var result = new ValidationResult(Info(draft), issues);
        if (result.Valid)
        {
            draft.ValidatedAt = clock.GetUtcNow();
            draft.ValidatedHash = draft.Hash;
        }
        Declare(draft, "revision.validated").With("valid", result.Valid).With("issues", issues.Count);
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return result with { Revision = Info(draft) };
    }

    /// <summary>
    /// Makes the draft live: validated, still based on what is live, and the published pointer,
    /// the superseded revision and the audit record moved in one transaction.
    /// </summary>
    public async Task<PublishResult> PublishAsync(string expectedHash, CancellationToken ct)
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
        var issues = ConfigValidator.Validate(config, await PublishedConfigAsync(app, ct));
        if (issues.Any(i => i.Severity == IssueSeverity.Error))
        {
            Declare(draft, "revision.published").Failed("validation").With("issues", issues.Count);
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

        Declare(draft, "revision.published").With("previous", previous?.Number);
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return new PublishResult(true, Info(draft), issues);
    }

    /// <summary>
    /// Makes an earlier published revision's configuration live again — as a new revision, so
    /// history only ever moves forward and the one rolled back from stays inspectable.
    /// </summary>
    public async Task<PublishResult> RollbackAsync(RollbackRequest request, AppChangeSource source, CancellationToken ct)
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
        var issues = ConfigValidator.Validate(config, liveConfig);
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
        };
        revision.ValidatedHash = revision.Hash;
        db.Revisions.Add(revision);
        await SupersedeAsync(app, AppRevisionStatus.RolledBack, ct);
        app.PublishedRevisionId = revision.Id;
        await RecordChangesAsync(revision, ConfigDiff.Between(liveConfig, config), source, ct);

        Declare(revision, "revision.rolled_back").With("from", live.Number).With("to", target.Number);
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return new PublishResult(true, Info(revision), issues);
    }

    /// <summary>Throws the draft away. Its revision and change log stay, marked discarded.</summary>
    public async Task DiscardAsync(string expectedHash, CancellationToken ct)
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
        Declare(draft, "revision.discarded");
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
    }

    // ------------------------------------------------------------------ internals

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

    private async Task<AppRevision> OpenDraftAsync(DynamicApp app, string? description, AppChangeSource source, CancellationToken ct)
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
        };
        db.Revisions.Add(draft);
        app.DraftRevisionId = draft.Id;
        return draft;
    }

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

    private async Task<AppConfig?> PublishedConfigAsync(DynamicApp app, CancellationToken ct) =>
        app.PublishedRevisionId is { } id ? (await DocumentAsync(id, ct)).Config : null;

    private async Task RecordChangesAsync(AppRevision revision, IReadOnlyList<ConfigChange> changes, AppChangeSource source, CancellationToken ct)
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
    private AuditEntry Declare(AppRevision revision, string action) =>
        (audit.Declared ?? audit.Record($"plugin.{DynamicAppsPlugin.PluginId}.{action}"))
            .For(ResourceType, revision.Id, $"r{revision.Number}")
            .With("instance", InstanceId)
            .With("number", revision.Number)
            .With("hash", revision.Hash);

    private static ContractConflictException Stale(string current) =>
        new($"The configuration changed since you read it (current hash {current}). Read it again and re-apply your change.");

    private static ContractConflictException NoDraft() =>
        new("There is no open draft. Make a change, or create a draft, first.");
}
