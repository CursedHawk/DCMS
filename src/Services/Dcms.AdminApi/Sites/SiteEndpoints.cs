using Dcms.PluginSdk.Abstractions;
using Dcms.Shared.Audit;
using Dcms.Shared.Audit.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Dcms.AdminApi.Sites.Git;
using Dcms.AdminApi.Tenancy;
using Dcms.Shared.Contracts.Events;
using Dcms.Shared.Contracts.Messaging;
using Dcms.Shared.Data.Cms;
using Dcms.Shared.Data.Sites;
using Dcms.Shared.Data.Tenancy;
using Dcms.Shared.Kernel.Abstractions;
using Dcms.Shared.Messaging;
using Dcms.Shared.Security;
using Dcms.Shared.Storage;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Dcms.Shared.Data.Rls;

using Dcms.Shared.Data;
namespace Dcms.AdminApi.Sites;

public static class SiteEndpoints
{
    /// <summary>
    /// Plugins that record what visitors do (<c>PluginManifest.TracksVisitors</c>), snapshotted
    /// when the routes are mapped: the registry is fixed for the life of the process. Null until
    /// then, which the consent check treats as "ask".
    /// </summary>
    private static IReadOnlyList<string>? _visitorTrackingPlugins;

    public static IEndpointRouteBuilder MapSiteEndpoints(this IEndpointRouteBuilder app)
    {
        _visitorTrackingPlugins = app.ServiceProvider.GetRequiredService<Dcms.PluginSdk.Abstractions.IPluginCatalog>()
            .Manifests.Where(m => m.TracksVisitors).Select(m => m.Id).ToList();

        app.MapGet("/api/admin/sites", async (SitesDbContext db, CancellationToken ct) =>
        {
            var sites = await db.Sites
                .OrderByDescending(s => s.UpdatedAt)
                .Select(s => new
                {
                    id = s.Id,
                    name = s.Name,
                    renderMode = s.RenderMode.ToString(),
                    activeBuildId = s.ActiveBuildId,
                    updatedAt = s.UpdatedAt,
                    // What environment policies name it by (ADR 0028).
                    environmentKey = s.EnvironmentKey ?? s.Id.ToString(),
                })
                .ToListAsync(ct);
            return Results.Ok(sites);
        }).RequirePermission(PlatformPermissions.SiteEdit);

        app.MapPost("/api/admin/sites", async (
            CreateSiteRequest body, SitesDbContext db, ITenantContext tenant, CancellationToken ct) =>
        {
            if (!tenant.HasTenant)
            {
                return Results.BadRequest(new { error = "Select a tenant first (X-Dcms-Tenant header required)." });
            }
            var mode = Enum.TryParse<SiteRenderMode>(body.RenderMode, ignoreCase: true, out var m)
                ? m : SiteRenderMode.StaticPrerender;
            var site = new Site
            {
                Id = Guid.NewGuid(),
                TenantId = tenant.TenantId!.Value,
                Name = body.Name,
                RenderMode = mode,
                // Git-backed modes seed their own starter file map on first open in
                // the editor (the builder / IDE knows what a new site should contain),
                // so a fresh site starts with an empty definition rather than a
                // server-invented one that would immediately be replaced.
                DraftDefinitionJson = body.Definition ?? "{}",
            };
            db.Sites.Add(site);
            await db.SaveChangesAsync(ct);
            return Results.Created($"/api/admin/sites/{site.Id}", new { id = site.Id });
        }).RequirePermission(PlatformPermissions.SiteEdit).WithAudit(AuditActions.SiteCreated, "site")
            .ManagedByDeployments("sites");

        app.MapGet("/api/admin/sites/{id:guid}", async (Guid id, SitesDbContext db, CancellationToken ct) =>
        {
            var site = await db.Sites.FirstOrDefaultAsync(s => s.Id == id, ct);
            if (site is null)
            {
                return Results.NotFound();
            }
            return Results.Ok(new
            {
                id = site.Id,
                name = site.Name,
                renderMode = site.RenderMode.ToString(),
                // What environment policies name the site by: the same in every environment (ADR 0026).
                environmentKey = site.KeyAcrossEnvironments,
                activeBuildId = site.ActiveBuildId,
                // Optimistic-concurrency baseline for the IDE: the version bumps on
                // every save, and each file's hash lets a granular save detect that
                // someone else changed that same file (see the PATCH endpoint below).
                definitionVersion = site.DefinitionVersion,
                definitionHashes = SiteFileMap.HashAll(SiteFileMap.Parse(site.DraftDefinitionJson)),
                definition = JsonDocument.Parse(site.DraftDefinitionJson).RootElement,
                staticBundle = site.StaticBundleKey is null ? null : new
                {
                    name = site.StaticBundleName,
                    size = site.StaticBundleSize,
                    fileCount = site.StaticBundleFileCount,
                    uploadedAt = site.StaticBundleUploadedAt,
                },
            });
        }).RequirePermission(PlatformPermissions.SiteEdit);

        // Upload a pre-built static bundle (Mode C): one .zip and/or loose files
        // (incl. a folder upload carrying relative paths). The files are sanitized
        // and normalized into a staged bundle; publishing snapshots it into a build.
        app.MapPost("/api/admin/sites/{id:guid}/upload", async (
            Guid id, HttpRequest request, SitesDbContext db, ITenantContext tenant,
            IObjectStorage storage, IOptions<StorageOptions> storageOptions, CancellationToken ct) =>
        {
            var site = await db.Sites.FirstOrDefaultAsync(s => s.Id == id, ct);
            if (site is null)
            {
                return Results.NotFound();
            }
            if (site.RenderMode != SiteRenderMode.StaticFiles)
            {
                return Results.BadRequest(new { error = "This site is not in static-files mode." });
            }
            if (!request.HasFormContentType)
            {
                return Results.BadRequest(new { error = "Expected a multipart file upload." });
            }

            // Raise the body-size ceiling for this endpoint only (default Kestrel is
            // ~30 MB). Must be set before the body is read.
            var sizeFeature = request.HttpContext.Features.Get<IHttpMaxRequestBodySizeFeature>();
            if (sizeFeature is { IsReadOnly: false })
            {
                sizeFeature.MaxRequestBodySize = StaticSiteFiles.MaxUploadBytes;
            }

            var form = await request.ReadFormAsync(ct);
            if (form.Files.Count == 0)
            {
                return Results.BadRequest(new { error = "No files were uploaded." });
            }

            StaticBundleBuilder.Result bundle;
            try
            {
                bundle = await StaticBundleBuilder.BuildAsync(form.Files, ct);
            }
            catch (StaticBundleException ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
            catch (InvalidDataException)
            {
                return Results.BadRequest(new { error = "A .zip file could not be read." });
            }

            var tenantId = tenant.TenantId!.Value;
            var uploadId = Guid.NewGuid();
            var key = StorageKeys.SiteBundleStaging(tenantId, site.Id, uploadId);
            await using (var upload = new MemoryStream(bundle.ZipBytes))
            {
                await storage.PutAsync(storageOptions.Value.SitesBucket, key, upload, bundle.ZipBytes.Length, "application/zip", ct);
            }

            site.StaticBundleKey = key;
            site.StaticBundleName = bundle.RootName ?? form.Files[0].FileName;
            site.StaticBundleSize = bundle.TotalBytes;
            site.StaticBundleFileCount = bundle.FileCount;
            site.StaticBundleUploadedAt = DateTimeOffset.UtcNow;
            site.UpdatedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(ct);

            return Results.Ok(new
            {
                fileCount = bundle.FileCount,
                size = bundle.TotalBytes,
                name = site.StaticBundleName,
                hasIndex = bundle.HasIndex,
            });
        }).RequirePermission(PlatformPermissions.SiteEdit).DisableAntiforgery().WithAudit(AuditActions.SiteUploaded, "site");

        // Build history for a site (status, source commit, log availability, which one is
        // live) — drives both the rollback UI and the IDE Deployments panel.
        app.MapGet("/api/admin/sites/{id:guid}/builds", async (Guid id, int? limit, SitesDbContext db, CancellationToken ct) =>
        {
            var site = await db.Sites.FirstOrDefaultAsync(s => s.Id == id, ct);
            if (site is null)
            {
                return Results.NotFound();
            }
            // Each publishing branch has its own live build (#277); release's is the site's.
            var live = await db.PublishBranches.Where(p => p.SiteId == id && p.ActiveBuildId != null)
                .Select(p => p.ActiveBuildId!.Value).ToListAsync(ct);
            if (site.ActiveBuildId is { } releaseLive) live.Add(releaseLive);
            var take = Math.Clamp(limit ?? 30, 1, 100);
            var builds = await db.Builds
                .Where(b => b.SiteId == id)
                .OrderByDescending(b => b.CreatedAt)
                .Take(take)
                .Select(b => new
                {
                    id = b.Id,
                    branch = b.Branch,
                    status = b.Status.ToString(),
                    gitCommitSha = b.GitCommitSha,
                    error = b.Error,
                    hasLog = b.LogObjectKey != null,
                    createdAt = b.CreatedAt,
                    completedAt = b.CompletedAt,
                })
                .ToListAsync(ct);
            return Results.Ok(builds.Select(b => new
            {
                b.id,
                branch = b.branch ?? Dcms.AdminApi.Sites.Git.SiteGitService.ReleaseBranch,
                b.status,
                b.gitCommitSha,
                shortSha = b.gitCommitSha is { Length: >= 7 } ? b.gitCommitSha[..7] : b.gitCommitSha,
                b.error,
                b.hasLog,
                b.createdAt,
                b.completedAt,
                // `active` for the IDE panel; `isActive` kept for the existing rollback UI.
                active = live.Contains(b.id),
                isActive = live.Contains(b.id),
            }));
        }).RequirePermission(PlatformPermissions.SiteEdit);

        // Roll back / forward: re-activate a previously succeeded build. Its artifacts
        // still live under their own prefix, so this is a pointer switch.
        app.MapPost("/api/admin/sites/{id:guid}/builds/{buildId:guid}/activate", async (
            Guid id, Guid buildId, SitesDbContext db, CancellationToken ct) =>
        {
            var site = await db.Sites.FirstOrDefaultAsync(s => s.Id == id, ct);
            if (site is null)
            {
                return Results.NotFound();
            }
            var build = await db.Builds.FirstOrDefaultAsync(b => b.Id == buildId && b.SiteId == id, ct);
            if (build is null)
            {
                return Results.NotFound();
            }
            if (build.Status != SiteBuildStatus.Succeeded)
            {
                return Results.BadRequest(new { error = "Only a succeeded build can be activated." });
            }
            if (build.Branch is not null)
            {
                // A branch's build goes live on that branch's domains, never on release's.
                var target = await db.PublishBranches.FirstOrDefaultAsync(p => p.SiteId == id && p.Branch == build.Branch, ct);
                if (target is null)
                {
                    return Results.BadRequest(new { error = $"The branch {build.Branch} no longer publishes, so its builds cannot go live." });
                }
                target.ActiveBuildId = build.Id;
            }
            else
            {
                site.ActiveBuildId = build.Id;
            }
            site.UpdatedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(ct);
            return Results.NoContent();
        }).RequirePermission(PlatformPermissions.SitePublish).WithAudit(AuditActions.SiteBuildActivated, "site")
            .ManagedByDeployments("sites");

        // Whole-map replace. Used for a full flush (publish) and as a fallback. An
        // optional `If-Match: <version>` guards against overwriting a newer draft:
        // the whole-map write clobbers every file, so it must lose to any change it
        // has not seen. Granular edits go through PATCH .../definition/files instead.
        app.MapPut("/api/admin/sites/{id:guid}/definition", async (
            Guid id, JsonElement definition, HttpRequest request,
            SitesDbContext db, CancellationToken ct) =>
        {
            var site = await db.Sites.FirstOrDefaultAsync(s => s.Id == id, ct);
            if (site is null)
            {
                return Results.NotFound();
            }
            if (request.Headers.TryGetValue("If-Match", out var ifMatch) &&
                int.TryParse(ifMatch.ToString(), out var expected) &&
                expected != site.DefinitionVersion)
            {
                return Results.Json(
                    new { error = "conflict", version = site.DefinitionVersion },
                    statusCode: StatusCodes.Status409Conflict);
            }
            site.DraftDefinitionJson = definition.GetRawText();
            site.DefinitionVersion++;
            site.UpdatedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(ct);

            var files = SiteFileMap.Parse(site.DraftDefinitionJson);
            return Results.Ok(new { version = site.DefinitionVersion, hashes = SiteFileMap.HashAll(files) });
        }).RequirePermission(PlatformPermissions.SiteEdit).WithAudit(AuditActions.SiteUpdated, "site");

        // Granular per-file save (Mode B IDE autosave). Instead of resending the whole
        // file map — which makes two people editing different files clobber each other
        // with their own stale snapshots — this merges only the named files into the
        // CURRENT stored map. Each entry carries the hash the client last saw for that
        // path; if the stored file has since changed (someone else saved it), that file
        // is reported as a conflict (409) and nothing is written. Edits to other files
        // never conflict, so concurrent work on separate files just merges.
        // Load the current user's working draft for a branch (Mode B IDE). Drafts are
        // per (site, user, branch): the "being worked on" version that autosaves flush
        // into, decoupled from git — commits are explicit. A first open with no draft
        // yet is seeded from the branch's git HEAD (recording BaseSha for divergence
        // detection at commit time).
        app.MapGet("/api/admin/sites/{id:guid}/ide", async (
            Guid id, string? branch, SitesDbContext db, ITenantContext tenant, CurrentUser user,
            Dcms.AdminApi.Sites.Git.SiteGitService git, SiteLiveUpdates live, IServiceProvider services, CancellationToken ct) =>
        {
            var site = await db.Sites.FirstOrDefaultAsync(s => s.Id == id, ct);
            if (site is null) return Results.NotFound();
            if (!site.RenderMode.IsGitBacked())
                return Results.BadRequest(new { error = "This site's source is not authored in DCMS, so it has no working draft." });

            // Provision the repo on first open (like GET /git) so a branch always exists.
            if (site.GitRepoFullName is null && git.Enabled && !string.IsNullOrWhiteSpace(tenant.TenantSlug))
            {
                var seed = SiteFileMap.Parse(site.DraftDefinitionJson);
                var info = await git.EnsureRepoAsync(tenant.TenantSlug!, site.Id, seed, null, null, ct);
                site.GitRepoFullName = info.RepoFullName;
                site.GitDefaultBranch = info.DefaultBranch;
                site.GitProvisionedAt ??= DateTimeOffset.UtcNow;
                site.UpdatedAt = DateTimeOffset.UtcNow;
                await db.SaveChangesAsync(ct);
            }

            // Back-fill the release branch on repos provisioned before it existed.
            if (site.GitRepoFullName is not null && git.Enabled)
                await git.EnsureReleaseBranchAsync(site.GitRepoFullName, ct);

            // Seeded from git HEAD, or the legacy DB draft while the repo isn't ready.
            return await GitWorkspaces.LoadDraftAsync(Workspace(site, live, services, tenant), branch,
                () => SiteFileMap.Parse(site.DraftDefinitionJson), db, git, user, ct);
        }).RequirePermission(PlatformPermissions.SiteEdit);

        // Granular per-file save into the current user's working draft for a branch.
        // Merges only the named files into the stored draft; each entry carries the
        // hash the client last saw, so the same account editing the same file+branch
        // from two tabs is reported as a conflict (409) instead of clobbering. Edits
        // to different files just merge. No git write happens here — that's on commit.
        app.MapPatch("/api/admin/sites/{id:guid}/ide/files", async (
            Guid id, string? branch, SaveFilesRequest body,
            SitesDbContext db, ITenantContext tenant, CurrentUser user,
            SiteLiveUpdates live, IServiceProvider services, CancellationToken ct) =>
        {
            var site = await db.Sites.FirstOrDefaultAsync(s => s.Id == id, ct);
            if (site is null) return Results.NotFound();
            return await GitWorkspaces.SaveFilesAsync(Workspace(site, live, services, tenant), branch, body, db, user, ct);
        }).RequirePermission(PlatformPermissions.SiteEdit).WithAudit(AuditActions.SiteFilesChanged, "site");

        app.MapPost("/api/admin/sites/{id:guid}/publish", async (
            Guid id, string? branch, SitesDbContext db, CmsDbContext cms, ITenantContext tenant,
            IEventPublisher events, CurrentUser user, Dcms.AdminApi.Sites.Git.SiteGitService git,
            SiteLiveUpdates live, CancellationToken ct) =>
        {
            var site = await db.Sites.FirstOrDefaultAsync(s => s.Id == id, ct);
            if (site is null)
            {
                return Results.NotFound();
            }

            // Git-backed (Modes A and B): release is a normal branch whose only special
            // power is that a push to it builds+deploys. Publishing = merge the current
            // branch into release. The frontend commits the working draft first, so this
            // ships committed work. Publishing while already on release just (re)builds
            // the release head.
            if (site.RenderMode.IsGitBacked() && site.GitRepoFullName is not null && git.Enabled)
            {
                var release = Dcms.AdminApi.Sites.Git.SiteGitService.ReleaseBranch;
                var sourceBranch = ResolveBranch(branch, site);
                await git.EnsureReleaseBranchAsync(site.GitRepoFullName, ct);
                try { await git.EnsureWebhookAsync(site.GitRepoFullName, ct); } catch { /* best effort */ }

                if (sourceBranch == release)
                {
                    // Already on release: deploy the current release head (force a fresh build
                    // even if the tree is unchanged — this is the "redeploy" affordance).
                    var buildId = await EnqueueBranchBuildAsync(db, cms, events, git, site, live, user.UserId, null, ct);
                    return Results.Accepted($"/api/admin/sites/{site.Id}/builds/{buildId}", new { released = true, buildId });
                }

                var outcome = await git.MergeAsync(site.GitRepoFullName, release, sourceBranch, ct);
                if (outcome.UpToDate)
                    return Results.Ok(new { released = false, upToDate = true });
                if (outcome.Merged)
                {
                    await AnnounceCommitAsync(live, site, release, outcome.Sha, $"Publish {sourceBranch} to {release}", user, ct);
                    return Results.Accepted($"/api/admin/sites/{site.Id}", new { released = true, sha = outcome.Sha, branch = release });
                }
                return Results.Ok(new
                {
                    released = false,
                    conflict = true,
                    @base = release,
                    head = sourceBranch,
                    files = outcome.Conflicts.Select(f => new
                    {
                        path = f.Path,
                        releaseContent = f.ReleaseContent,
                        branchContent = f.BranchContent,
                        baseContent = f.BaseContent,
                    }),
                });
            }

            // StaticFiles publishes the staged upload; the snapshot records which
            // bundle to extract so the build is self-contained (race-free rollback).
            string snapshot;
            if (site.RenderMode == SiteRenderMode.StaticFiles)
            {
                if (site.StaticBundleKey is null)
                {
                    return Results.BadRequest(new { error = "Upload your website files before publishing." });
                }
                snapshot = JsonSerializer.Serialize(new
                {
                    bundleKey = site.StaticBundleKey,
                    name = site.StaticBundleName,
                    size = site.StaticBundleSize,
                    fileCount = site.StaticBundleFileCount,
                });
            }
            else
            {
                snapshot = site.DraftDefinitionJson;
            }

            var tenantId = tenant.TenantId!.Value;
            var build = new SiteBuild
            {
                Id = Guid.NewGuid(),
                TenantId = tenantId,
                SiteId = site.Id,
                Status = SiteBuildStatus.Queued,
                DefinitionSnapshotJson = snapshot,
            };
            build.ArtifactPrefix = $"{tenantId}/{site.Id}/{build.Id}";
            db.Builds.Add(build);
            await db.SaveChangesAsync(ct);

            var mode = site.RenderMode.ToString();
            await live.BuildRecordedAsync(tenantId, build, site.Name, ct);
            await events.PublishAsync(Subjects.SitePublishSubjectFor(mode), new SitePublishRequested(
                Guid.NewGuid(), DateTimeOffset.UtcNow, tenantId, site.Id, build.Id,
                mode, await AnalyticsEnabledAsync(cms, tenantId, ct)), ct,
                // One build, one request: a repeated publish of it is dropped by JetStream (ADR 0024).
                messageId: $"build:{build.Id}");
            await live.BuildQueuedAsync(tenantId, build, user.UserId, ct);

            return Results.Accepted($"/api/admin/sites/{site.Id}/builds/{build.Id}", new { buildId = build.Id });
        }).RequirePermission(PlatformPermissions.SitePublish).WithAudit(AuditActions.SitePublishRequested, "site")
            .ManagedByDeployments("sites");

        // --- Git backend (Forgejo): provision a repo for a Mode B site and seed it. ---
        // Provisioning is idempotent: ensures the tenant org + site repo exist and, on
        // first creation, commits the current draft file map as the initial history.
        app.MapPost("/api/admin/sites/{id:guid}/git/provision", async (
            Guid id, SitesDbContext db, ITenantContext tenant, Dcms.AdminApi.Sites.Git.SiteGitService git,
            CancellationToken ct) =>
        {
            if (!git.Enabled) return Results.Problem("Git backend is not configured.", statusCode: 503);
            if (!tenant.HasTenant || string.IsNullOrWhiteSpace(tenant.TenantSlug))
                return Results.BadRequest(new { error = "Select a tenant first (X-Dcms-Tenant header required)." });

            var site = await db.Sites.FirstOrDefaultAsync(s => s.Id == id, ct);
            if (site is null) return Results.NotFound();
            if (!site.RenderMode.IsGitBacked())
                return Results.BadRequest(new { error = "Git source is only available for sites whose source is authored in DCMS." });

            var files = SiteFileMap.Parse(site.DraftDefinitionJson);
            var info = await git.EnsureRepoAsync(tenant.TenantSlug!, site.Id, files, authorName: null, authorEmail: null, ct);

            site.GitRepoFullName = info.RepoFullName;
            site.GitDefaultBranch = info.DefaultBranch;
            site.GitProvisionedAt ??= DateTimeOffset.UtcNow;
            site.UpdatedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(ct);

            return Results.Ok(new
            {
                repo = info.RepoFullName,
                branch = info.DefaultBranch,
                head = info.HeadSha,
                httpUrl = info.HttpCloneUrl,
                sshUrl = info.SshCloneUrl,
            });
        }).RequirePermission(PlatformPermissions.SiteEdit).WithAudit(AuditActions.GitRepoProvisioned, "site");

        // Git status for a Mode B site. Auto-provisions the repo on first open so the
        // IDE never needs a manual provision step. Returns clone URLs for the panel.
        app.MapGet("/api/admin/sites/{id:guid}/git", async (
            Guid id, SitesDbContext db, ITenantContext tenant, Dcms.AdminApi.Sites.Git.SiteGitService git,
            CurrentUser user, Dcms.AdminApi.Sites.Git.RepoAccessReconciler repoAccess, CancellationToken ct) =>
        {
            var site = await db.Sites.FirstOrDefaultAsync(s => s.Id == id, ct);
            if (site is null) return Results.NotFound();
            if (!site.RenderMode.IsGitBacked() || !git.Enabled)
                return Results.Ok(new { enabled = false });

            if (site.GitRepoFullName is null && !string.IsNullOrWhiteSpace(tenant.TenantSlug))
            {
                var files = SiteFileMap.Parse(site.DraftDefinitionJson);
                var info = await git.EnsureRepoAsync(tenant.TenantSlug!, site.Id, files, null, null, ct);
                site.GitRepoFullName = info.RepoFullName;
                site.GitDefaultBranch = info.DefaultBranch;
                site.GitProvisionedAt ??= DateTimeOffset.UtcNow;
                site.UpdatedAt = DateTimeOffset.UtcNow;
                await db.SaveChangesAsync(ct);
            }
            if (site.GitRepoFullName is null) return Results.Ok(new { enabled = true, provisioned = false });

            // Self-heal: bring the caller's Forgejo collaborator access in line with
            // their per-site repo permissions whenever they open the git panel.
            if (tenant.TenantId is Guid tid && user.UserId is Guid uid && !string.IsNullOrWhiteSpace(user.Email))
                await repoAccess.ReconcileUserSiteAsync(tid, uid, user.Email!, site, ct, user.IsSuperAdmin);

            var (http, ssh) = git.CloneUrls(site.GitRepoFullName);
            return Results.Ok(new
            {
                enabled = true,
                provisioned = true,
                repo = site.GitRepoFullName,
                branch = site.GitDefaultBranch ?? Dcms.AdminApi.Sites.Git.SiteGitService.DefaultBranch,
                httpUrl = http,
                sshUrl = ssh,
                provisionedAt = site.GitProvisionedAt,
            });
        }).RequirePermission(PlatformPermissions.SiteEdit);

        app.MapGet("/api/admin/sites/{id:guid}/git/branches", async (
            Guid id, SitesDbContext db, ITenantContext tenant, SiteLiveUpdates live, IServiceProvider services,
            Dcms.AdminApi.Sites.Git.SiteGitService git, CancellationToken ct) =>
        {
            var site = await db.Sites.FirstOrDefaultAsync(s => s.Id == id, ct);
            if (site is null) return Results.Ok(Array.Empty<object>());
            return await GitWorkspaces.BranchesAsync(Workspace(site, live, services, tenant), git, ct);
        }).RequirePermission(PlatformPermissions.SiteEdit);

        app.MapGet("/api/admin/sites/{id:guid}/git/history", async (
            Guid id, string? branch, int? limit, SitesDbContext db, ITenantContext tenant, SiteLiveUpdates live,
            IServiceProvider services, Dcms.AdminApi.Sites.Git.SiteGitService git, CancellationToken ct) =>
        {
            var site = await db.Sites.FirstOrDefaultAsync(s => s.Id == id, ct);
            if (site is null) return Results.Ok(Array.Empty<object>());
            return await GitWorkspaces.HistoryAsync(Workspace(site, live, services, tenant), branch, limit, git, ct);
        }).RequirePermission(PlatformPermissions.SiteEdit);

        // Source-control "changes": the current user's working draft on a branch vs that
        // branch's git HEAD. Drives the changed-files list and the per-file diff view.
        // Content is included (both sides) so the frontend Monaco diff needs no extra
        // round-trip; oversized/binary files return null content (marked truncated).
        app.MapGet("/api/admin/sites/{id:guid}/git/changes", async (
            Guid id, string? branch, SitesDbContext db, CurrentUser user, ITenantContext tenant, SiteLiveUpdates live,
            IServiceProvider services, Dcms.AdminApi.Sites.Git.SiteGitService git, CancellationToken ct) =>
        {
            var site = await db.Sites.FirstOrDefaultAsync(s => s.Id == id, ct);
            if (site is null) return Results.Ok(Array.Empty<object>());
            return await GitWorkspaces.ChangesAsync(Workspace(site, live, services, tenant), branch, db, user, git, ct);
        }).RequirePermission(PlatformPermissions.SiteEdit);

        // Commit the current user's working draft (on `branch`) to git — to that same
        // branch, an existing `targetBranch`, or a freshly created `newBranch`. The user
        // is the git author; the platform is the committer. A commit to `release` fires
        // the push webhook → build+deploy. Committing to the SAME branch after it moved
        // underneath the draft returns 409 `branch-moved` (reconcile/reload; full merge
        // is planned).
        app.MapPost("/api/admin/sites/{id:guid}/git/commit", async (
            Guid id, CommitRequest body, SitesDbContext db, ITenantContext tenant, CurrentUser user,
            Dcms.AdminApi.Sites.Git.SiteGitService git, SiteLiveUpdates live, IServiceProvider services, CancellationToken ct) =>
        {
            if (!git.Enabled) return Results.Problem("Git backend is not configured.", statusCode: 503);
            var site = await db.Sites.FirstOrDefaultAsync(s => s.Id == id, ct);
            if (site is null) return Results.NotFound();
            return await GitWorkspaces.CommitAsync(Workspace(site, live, services, tenant), body, db, user, git, ct);
        }).RequirePermission(PlatformPermissions.SiteEdit).WithAudit(AuditActions.GitCommitted, "site");

        // Create a branch off another (defaults to the site's default branch).
        app.MapPost("/api/admin/sites/{id:guid}/git/branches", async (
            Guid id, CreateBranchRequest body, SitesDbContext db, ITenantContext tenant, SiteLiveUpdates live,
            IServiceProvider services, Dcms.AdminApi.Sites.Git.SiteGitService git, CancellationToken ct) =>
        {
            var site = await db.Sites.FirstOrDefaultAsync(s => s.Id == id, ct);
            if (site is null) return Results.BadRequest(new { error = "This site has no git repository." });
            return await GitWorkspaces.CreateBranchAsync(Workspace(site, live, services, tenant), body, git, ct);
        }).RequirePermission(PlatformPermissions.SiteEdit).WithAudit(AuditActions.GitBranchCreated, "site");

        // Pre-merge review: what merging `head` into the release branch would bring
        // (files changed + commit count).
        app.MapGet("/api/admin/sites/{id:guid}/git/compare", async (
            Guid id, string head, string? @base, SitesDbContext db, ITenantContext tenant, SiteLiveUpdates live,
            IServiceProvider services, Dcms.AdminApi.Sites.Git.SiteGitService git, CancellationToken ct) =>
        {
            var site = await db.Sites.FirstOrDefaultAsync(s => s.Id == id, ct);
            if (site is null) return Results.Ok(new { totalCommits = 0, files = Array.Empty<object>() });
            return await GitWorkspaces.CompareAsync(Workspace(site, live, services, tenant), head, @base, git, ct);
        }).RequirePermission(PlatformPermissions.SiteEdit);

        // Merge `head` into `base` (defaults to release). A real 3-way merge: clean changes
        // auto-merge, only true conflicts need resolution. A clean merge into release fires a
        // build. Returns { merged, sha } | { upToDate } | { conflict, files:[{ path,
        // releaseContent (base/ours), branchContent (incoming/theirs), baseContent }] }.
        app.MapPost("/api/admin/sites/{id:guid}/git/merge", async (
            Guid id, MergeRequest body, SitesDbContext db, CurrentUser user, ITenantContext tenant, IServiceProvider services,
            Dcms.AdminApi.Sites.Git.SiteGitService git, SiteLiveUpdates live, CancellationToken ct) =>
        {
            if (!git.Enabled) return Results.Problem("Git backend is not configured.", statusCode: 503);
            var site = await db.Sites.FirstOrDefaultAsync(s => s.Id == id, ct);
            if (site?.GitRepoFullName is null)
                return Results.BadRequest(new { error = "This site has no git repository." });
            return await GitWorkspaces.MergeAsync(Workspace(site, live, services, tenant), body, user, git, ct);
        }).RequirePermission(PlatformPermissions.SitePublish).WithAudit(AuditActions.GitMerged, "site")
            .ManagedByDeployments("sites");

        // Complete a conflicted merge of `head` into `base` (defaults to release): apply the
        // caller's per-file resolutions onto the auto-merged tree and commit the two-parent
        // merge (→ build when base is release). A null resolution value deletes the file.
        app.MapPost("/api/admin/sites/{id:guid}/git/merge/resolve", async (
            Guid id, ResolveMergeRequest body, SitesDbContext db, CurrentUser user, ITenantContext tenant, IServiceProvider services,
            Dcms.AdminApi.Sites.Git.SiteGitService git, SiteLiveUpdates live, CancellationToken ct) =>
        {
            if (!git.Enabled) return Results.Problem("Git backend is not configured.", statusCode: 503);
            var site = await db.Sites.FirstOrDefaultAsync(s => s.Id == id, ct);
            if (site?.GitRepoFullName is null)
                return Results.BadRequest(new { error = "This site has no git repository." });
            return await GitWorkspaces.ResolveMergeAsync(Workspace(site, live, services, tenant), body, user, git, ct);
        }).RequirePermission(PlatformPermissions.SitePublish).WithAudit(AuditActions.GitMergeResolved, "site")
            .ManagedByDeployments("sites");

        // Restore an earlier commit's tree into the current user's working draft on a
        // branch (does not commit — the user reviews the diff and commits explicitly).
        app.MapPost("/api/admin/sites/{id:guid}/git/restore", async (
            Guid id, GitRestoreRequest body, string? branch, SitesDbContext db, ITenantContext tenant,
            CurrentUser user, SiteLiveUpdates live, IServiceProvider services, Dcms.AdminApi.Sites.Git.SiteGitService git, CancellationToken ct) =>
        {
            var site = await db.Sites.FirstOrDefaultAsync(s => s.Id == id, ct);
            if (site is null) return Results.NotFound();
            return await GitWorkspaces.RestoreAsync(Workspace(site, live, services, tenant), body, branch, db, user, git, ct);
        }).RequirePermission(PlatformPermissions.SiteEdit).WithAudit(AuditActions.GitRestored, "site");

        // Forgejo push webhook: builds+deploys the site on a push/merge to the RELEASE
        // branch — this is what "publish" lands on. HMAC-verified (no user auth). Cross-
        // tenant — Forgejo doesn't know tenants, so the site is resolved by repo name
        // (owner role bypasses RLS). Any push to `release` builds (app commit, merge, or
        // external `git push`); pushes to feature/dev branches never build.
        app.MapPost("/api/internal/git/webhook", async (
            HttpRequest request, SitesDbContext db, CmsDbContext cms, IEventPublisher events,
            Dcms.AdminApi.Sites.Git.SiteGitService git, SiteLiveUpdates live,
            IOptions<Dcms.AdminApi.Sites.Git.ForgejoOptions> gitOptions,
            IAuditRecorder audit, AuditScope scope,
            ILoggerFactory loggerFactory, CancellationToken ct) =>
        {
            var log = loggerFactory.CreateLogger("GitWebhook");
            using var reader = new StreamReader(request.Body);
            var raw = await reader.ReadToEndAsync(ct);

            // Never authenticate against an unconfigured secret: HMAC over an empty
            // key is predictable, so an empty secret would make the webhook forgeable.
            var secret = gitOptions.Value.WebhookSecret;
            if (string.IsNullOrWhiteSpace(secret))
            {
                log.LogWarning("Git webhook rejected: WebhookSecret is not configured.");
                audit.Record(AuditActions.WebhookRejected)
                    .Platform()
                    .As(AuditCategory.Security, AuditSeverity.Warning)
                    .Denied("WebhookSecret is not configured");
                return Results.Unauthorized();
            }

            // Verify HMAC-SHA256(body, secret) against the signature header.
            var signature = request.Headers["X-Gitea-Signature"].FirstOrDefault()
                ?? request.Headers["X-Forgejo-Signature"].FirstOrDefault();
            var expected = Convert.ToHexString(HMACSHA256.HashData(
                Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes(raw)))
                .ToLowerInvariant();
            if (signature is null || !CryptographicOperations.FixedTimeEquals(
                    Encoding.ASCII.GetBytes(signature), Encoding.ASCII.GetBytes(expected)))
            {
                // Somebody posted to this route without the shared secret. Worth a record on
                // its own: it is either a misconfigured Forgejo or somebody probing.
                audit.Record(AuditActions.WebhookRejected)
                    .Platform()
                    .As(AuditCategory.Security, AuditSeverity.Warning)
                    .Denied(signature is null ? "no signature header" : "signature mismatch");
                return Results.Unauthorized();
            }

            // The HMAC proves the push came from Forgejo. It does not say who pushed — the
            // payload names a git author, which is a self-asserted string, not an identity
            // this platform authenticated. So: a webhook actor, inferred, never a user.
            scope.Actor = new AuditActor(
                ActorKind.Webhook, null, "forgejo", "Forgejo webhook", AuditAttribution.Inferred);

            using var doc = JsonDocument.Parse(raw);
            var root = doc.RootElement;
            if (!root.TryGetProperty("ref", out var refEl) ||
                !root.TryGetProperty("repository", out var repoEl))
            {
                return Results.Ok(new { ignored = "not a push" });
            }
            var gitRef = refEl.GetString() ?? "";
            var repoFull = repoEl.TryGetProperty("full_name", out var fn) ? fn.GetString() ?? "" : "";
            var afterSha = root.TryGetProperty("after", out var af) ? af.GetString() : null;

            // A signed webhook with no tenant: the repository name is what says whose site this is,
            // so that one lookup is platform-wide and everything after it acts as the site's tenant.
            // ReapStaleBuildsAsync and AnalyticsEnabledAsync, below, inherit that (ADR 0015).
            Site? site;
            RepoBinding? binding = null;
            using (RlsScope.Platform())
            {
                site = await db.Sites.IgnoreTenantFilter()
                    .FirstOrDefaultAsync(s => s.GitRepoFullName == repoFull, ct);
                if (site is null)
                {
                    binding = await db.RepoBindings.IgnoreTenantFilter().AsNoTracking()
                        .FirstOrDefaultAsync(r => r.RepoFullName == repoFull, ct);
                }
            }
            if (binding is not null)
            {
                return await RepoPushedAsync(binding, gitRef, afterSha, events, audit, ct);
            }
            if (site is null) return Results.Ok(new { ignored = "unknown repo" });
            // The site's own environment (ADR 0028): its build rows are that environment's.
            using var rls = RlsScope.Environment(site.TenantId, site.EnvironmentId);

            // Pushes to release build+deploy, and so do pushes to the site's publishing branches
            // (#277), each onto its own domains. Every other branch is work in progress.
            const string heads = "refs/heads/";
            if (!gitRef.StartsWith(heads, StringComparison.Ordinal)) return Results.Ok(new { ignored = "not a branch" });
            var branch = gitRef[heads.Length..];
            var publishBranch = PublishBranchKey(branch);
            if (publishBranch is not null &&
                !await db.PublishBranches.IgnoreTenantFilter().AnyAsync(p => p.SiteId == site.Id && p.Branch == publishBranch, ct))
            {
                return Results.Ok(new { ignored = "not a publishing branch" });
            }

            // The push is news for anyone with the site open regardless of what happens to the
            // build below -- including a push made with a plain `git push` from outside the
            // platform entirely, which is otherwise completely invisible to the IDE. Announced
            // BEFORE the coalescing check on purpose: a push that gets folded into a build
            // already in flight still moved the branch, and that is the half people need.
            //
            // The pusher's name comes off the payload and is therefore self-asserted, exactly
            // as the audit comment above says: a label to show, never an identity, so no actor
            // id travels with it.
            await AnnounceCommitAsync(
                live, site, branch, afterSha, PushedCommitMessage(root), PushedAuthor(root), null, ct);

            // Fail builds wedged in Queued/Building past the timeout so a crashed builder
            // can't permanently block this site (the coalescing check below would skip
            // forever otherwise).
            await ReapStaleBuildsAsync(db, site.Id, ct);

            // Coalesce: never stack builds for the same site and branch.
            var inFlight = await db.Builds.IgnoreTenantFilter().AnyAsync(
                b => b.SiteId == site.Id && b.Branch == publishBranch &&
                     (b.Status == SiteBuildStatus.Queued || b.Status == SiteBuildStatus.Building), ct);
            if (inFlight) return Results.Ok(new { skipped = "build in flight" });

            var files = await git.ReadFilesAsync(repoFull, afterSha ?? branch, ct);
            var build = new SiteBuild
            {
                Id = Guid.NewGuid(),
                TenantId = site.TenantId,
                SiteId = site.Id,
                Branch = publishBranch,
                Status = SiteBuildStatus.Queued,
                DefinitionSnapshotJson = SiteFileMap.Serialize(files),
                GitCommitSha = afterSha,
            };
            build.ArtifactPrefix = $"{site.TenantId}/{site.Id}/{build.Id}";
            db.Builds.Add(build);

            // Before the save, so the record commits with the build row it describes. The
            // commit sha is the whole point: it is the only thing that ties this build back to
            // what a person actually wrote, since the pusher's identity is not something the
            // HMAC establishes.
            audit.Declared?
                .For("site", site.Id, site.Name)
                .InTenant(site.TenantId)
                .With("repo", repoFull)
                .With("branch", branch)
                .With("commit", afterSha)
                .With("build_id", build.Id);

            await db.SaveChangesAsync(ct);

            var pushedMode = site.RenderMode.ToString();
            await live.BuildRecordedAsync(site.TenantId, build, site.Name, ct);
            await events.PublishAsync(Subjects.SitePublishSubjectFor(pushedMode), new SitePublishRequested(
                Guid.NewGuid(), DateTimeOffset.UtcNow, site.TenantId, site.Id, build.Id,
                pushedMode, await AnalyticsEnabledAsync(cms, site.TenantId, ct)), ct,
                messageId: $"build:{build.Id}");

            await live.BuildQueuedAsync(site.TenantId, build, null, ct);

            log.LogInformation("Queued build {BuildId} from git push {Sha} to {Repo}", build.Id, afterSha, repoFull);
            return Results.Ok(new { buildId = build.Id });
        }).AllowAnonymous().WithAudit(AuditActions.GitPushReceived, "site", AuditCategory.System);

        // Full step log (install + build output) for a build, as plain text. This is what
        // lets a user see exactly why a build failed, inside the IDE.
        app.MapGet("/api/admin/sites/{id:guid}/builds/{buildId:guid}/log", async (
            Guid id, Guid buildId, SitesDbContext db, IObjectStorage storage,
            IOptions<StorageOptions> storageOptions, CancellationToken ct) =>
        {
            var build = await db.Builds.FirstOrDefaultAsync(b => b.Id == buildId && b.SiteId == id, ct);
            if (build is null) return Results.NotFound();
            if (build.LogObjectKey is null)
                return Results.Text(build.Error ?? "No build log is available for this build.", "text/plain");
            try
            {
                await using var stream = await storage.GetAsync(storageOptions.Value.SitesBucket, build.LogObjectKey, ct);
                using var mem = new MemoryStream();
                await stream.CopyToAsync(mem, ct);
                return Results.Text(Encoding.UTF8.GetString(mem.ToArray()), "text/plain; charset=utf-8");
            }
            catch
            {
                return Results.Text(build.Error ?? "The build log could not be retrieved.", "text/plain");
            }
        }).RequirePermission(PlatformPermissions.SiteEdit);

        // Force a fresh build+deploy of the current release head, even if its tree is
        // unchanged (recovers from a failed build or a wedged queue — the tree-diff no-op
        // in the normal push path can't otherwise re-trigger).
        app.MapPost("/api/admin/sites/{id:guid}/builds", async (
            Guid id, string? branch, SitesDbContext db, CmsDbContext cms, IEventPublisher events, CurrentUser user,
            Dcms.AdminApi.Sites.Git.SiteGitService git, SiteLiveUpdates live, CancellationToken ct) =>
        {
            var site = await db.Sites.FirstOrDefaultAsync(s => s.Id == id, ct);
            if (site is null) return Results.NotFound();
            if (!site.RenderMode.IsGitBacked() || site.GitRepoFullName is null || !git.Enabled)
                return Results.BadRequest(new { error = "This site has no git-backed release to rebuild." });
            var publishBranch = string.IsNullOrWhiteSpace(branch) ? null : PublishBranchKey(branch.Trim());
            if (publishBranch is not null && !await db.PublishBranches.AnyAsync(p => p.SiteId == id && p.Branch == publishBranch, ct))
                return Results.BadRequest(new { error = $"The branch {publishBranch} does not publish." });
            var buildId = await EnqueueBranchBuildAsync(db, cms, events, git, site, live, user.UserId, publishBranch, ct);
            return Results.Accepted($"/api/admin/sites/{site.Id}/builds/{buildId}", new { buildId });
        }).RequirePermission(PlatformPermissions.SitePublish).WithAudit(AuditActions.SiteBuildCreated, "site");

        return app;
    }

    /// <summary>
    /// Tells everyone with this site open that <paramref name="branch"/> moved.
    ///
    /// <para>A no-op without a sha: a merge that changed nothing has none, and announcing a
    /// commit that does not exist would put a phantom row in every open editor's history.</para>
    /// </summary>
    private static Task AnnounceCommitAsync(
        SiteLiveUpdates live, Site site, string branch, string? sha, string? message,
        CurrentUser user, CancellationToken ct) =>
        AnnounceCommitAsync(live, site, branch, sha, message, user.Name ?? user.Email, user.UserId, ct);

    private static async Task AnnounceCommitAsync(
        SiteLiveUpdates live, Site site, string branch, string? sha, string? message,
        string? author, Guid? actorUserId, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(sha)) return;
        await live.CommitPushedAsync(site.TenantId, new CommitUpdate(
            SiteId: site.Id,
            Branch: branch,
            Sha: sha,
            ShortSha: SiteLiveUpdates.Short(sha)!,
            Message: message,
            Author: author,
            ActorUserId: actorUserId,
            OccurredAt: DateTimeOffset.UtcNow), ct);
    }

    /// <summary>
    /// A push to a plugin's repository (ADR 0029): the platform builds nothing from it, the
    /// owning plugin does. It is told on <c>dcms.repo.pushed</c>, in the repository's
    /// environment, and acts on its own (a Functions instance builds a deployment from release).
    /// </summary>
    private static async Task<IResult> RepoPushedAsync(
        RepoBinding binding, string gitRef, string? afterSha, IEventPublisher events, IAuditRecorder audit, CancellationToken ct)
    {
        const string heads = "refs/heads/";
        if (!gitRef.StartsWith(heads, StringComparison.Ordinal)) return Results.Ok(new { ignored = "not a branch" });
        var branch = gitRef[heads.Length..];

        using var rls = RlsScope.Environment(binding.TenantId, binding.EnvironmentId);
        var pushed = new Dcms.PluginSdk.Abstractions.Platform.RepoPushed(binding.InstanceId, binding.EnvironmentId, binding.PluginId, branch, afterSha);
        var name = Dcms.PluginSdk.Abstractions.Contracts.ContractIds.EventName(typeof(Dcms.PluginSdk.Abstractions.Platform.RepoPushed));
        var message = new PluginEventPublished(
            Guid.CreateVersion7(), DateTimeOffset.UtcNow, binding.TenantId, "dcms", name,
            JsonSerializer.Serialize(pushed, Dcms.PluginSdk.Runtime.Contracts.ContractDescriptorBuilder.Json));
        // Keyed on the push, so Forgejo redelivering it is one event.
        await events.PublishAsync(Subjects.PluginEvent("dcms", name), message, ct,
            messageId: $"repo:{binding.Id}:{branch}:{afterSha}");

        audit.Declared?
            .For("repository", binding.Id, binding.RepoFullName)
            .InTenant(binding.TenantId)
            .With("branch", branch)
            .With("commit", afterSha);
        return Results.Ok(new { notified = binding.PluginId });
    }

    /// <summary>The head commit's subject line out of a Forgejo push payload, if it has one.</summary>
    private static string? PushedCommitMessage(JsonElement root) =>
        root.TryGetProperty("head_commit", out var head) && head.ValueKind == JsonValueKind.Object
        && head.TryGetProperty("message", out var msg)
            ? msg.GetString()?.Split('\n')[0]
            : null;

    /// <summary>The pusher's display name off the payload. Self-asserted: a label, not an identity.</summary>
    private static string? PushedAuthor(JsonElement root) =>
        root.TryGetProperty("pusher", out var pusher) && pusher.ValueKind == JsonValueKind.Object
        && pusher.TryGetProperty("username", out var name)
            ? name.GetString()
            : null;

    /// <summary>
    /// A publishing branch's key: null for <c>release</c>, whose builds and domains are the
    /// site's own, else the branch name (#277).
    /// </summary>
    internal static string? PublishBranchKey(string branch) =>
        branch == Dcms.AdminApi.Sites.Git.SiteGitService.ReleaseBranch ? null : branch;

    /// <summary>Queue a build of the current tree of <c>release</c> (<paramref name="publishBranch"/>
    /// null) or of a publishing branch, and request it. Reaps any stale in-flight builds first so a
    /// wedged queue can't block the new one.</summary>
    internal static async Task<Guid> EnqueueBranchBuildAsync(
        SitesDbContext db, CmsDbContext cms, IEventPublisher events,
        Dcms.AdminApi.Sites.Git.SiteGitService git, Site site, SiteLiveUpdates live,
        Guid? actorUserId, string? publishBranch, CancellationToken ct)
    {
        await ReapStaleBuildsAsync(db, site.Id, ct);
        var gitBranch = publishBranch ?? Dcms.AdminApi.Sites.Git.SiteGitService.ReleaseBranch;
        var head = await git.HeadShaAsync(site.GitRepoFullName!, gitBranch, ct);
        var files = await git.ReadFilesAsync(site.GitRepoFullName!, gitBranch, ct);
        var build = new SiteBuild
        {
            Id = Guid.NewGuid(),
            TenantId = site.TenantId,
            SiteId = site.Id,
            Branch = publishBranch,
            Status = SiteBuildStatus.Queued,
            DefinitionSnapshotJson = SiteFileMap.Serialize(files),
            GitCommitSha = head,
        };
        build.ArtifactPrefix = $"{site.TenantId}/{site.Id}/{build.Id}";
        db.Builds.Add(build);
        await db.SaveChangesAsync(ct);
        var releaseMode = site.RenderMode.ToString();
        await live.BuildRecordedAsync(site.TenantId, build, site.Name, ct);
        await events.PublishAsync(Subjects.SitePublishSubjectFor(releaseMode), new SitePublishRequested(
            Guid.NewGuid(), DateTimeOffset.UtcNow, site.TenantId, site.Id, build.Id,
            releaseMode, await AnalyticsEnabledAsync(cms, site.TenantId, ct)), ct,
            messageId: $"build:{build.Id}");
        await live.BuildQueuedAsync(site.TenantId, build, actorUserId, ct);
        return build.Id;
    }

    /// <summary>
    /// Latest-wins catch-up (BUG-01). The webhook coalesces: a push that lands while a build
    /// is already running is dropped, because its snapshot would only stack another build.
    /// Nothing else ever built that dropped commit, so the live site could stay on the older
    /// tree indefinitely though the IDE said "released". This runs at the far end of every
    /// build (success or failure), re-reads the <c>release</c> head, and — if it has moved
    /// past what the finished build compiled and nothing is already in flight — queues one
    /// catch-up build of the current head. Any number of pushes that overlapped the build
    /// collapse into that single build of the latest tree.
    ///
    /// <para>Swallows everything: this is a best-effort tail on a notification consumer, and a
    /// throw here would fail the terminal message and re-run a notification that already
    /// landed. A missed catch-up is recoverable with a manual redeploy; a duplicated
    /// "your site is live" is not.</para>
    /// </summary>
    internal static async Task ContinueIfReleaseMovedAsync(
        IServiceProvider scope, Guid tenantId, Guid siteId, Guid completedBuildId, CancellationToken ct)
    {
        try
        {
            var git = scope.GetRequiredService<Dcms.AdminApi.Sites.Git.SiteGitService>();
            if (!git.Enabled) return;

            // Runs from a build's terminal event, with no ambient tenant; the event names it (ADR
            // 0015), and the site its environment (ADR 0028), whose builds and release these are.
            var db = scope.GetRequiredService<SitesDbContext>();
            Guid environmentId;
            using (RlsScope.Platform())
            {
                environmentId = await db.Sites.IgnoreQueryFilters().AsNoTracking()
                    .Where(s => s.Id == siteId && s.TenantId == tenantId).Select(s => s.EnvironmentId).FirstOrDefaultAsync(ct);
            }
            using var rls = RlsScope.Environment(tenantId, environmentId == Guid.Empty ? tenantId : environmentId);
            var site = await db.Sites.IgnoreTenantFilter()
                .FirstOrDefaultAsync(s => s.Id == siteId && s.TenantId == tenantId, ct);
            if (site is null || !site.RenderMode.IsGitBacked() || site.GitRepoFullName is null) return;

            var built = await db.Builds.AsNoTracking().IgnoreTenantFilter()
                .Where(b => b.Id == completedBuildId)
                .Select(b => new { b.GitCommitSha, b.Branch })
                .FirstOrDefaultAsync(ct);
            var builtSha = built?.GitCommitSha;

            // The branch the finished build published (#277); one that stopped publishing since
            // has nothing to catch up.
            var publishBranch = built?.Branch;
            if (publishBranch is not null &&
                !await db.PublishBranches.IgnoreTenantFilter().AnyAsync(p => p.SiteId == site.Id && p.Branch == publishBranch, ct))
                return;

            var release = publishBranch ?? Dcms.AdminApi.Sites.Git.SiteGitService.ReleaseBranch;
            var head = await git.HeadShaAsync(site.GitRepoFullName, release, ct);
            if (string.IsNullOrEmpty(head) ||
                string.Equals(head, builtSha, StringComparison.OrdinalIgnoreCase))
                return;

            // Something is already building (a manual redeploy, or a redelivery of this same
            // terminal event that already queued the catch-up): let it be — its own completion
            // re-checks the head, so the chain still converges on the latest commit.
            var inFlight = await db.Builds.IgnoreTenantFilter().AnyAsync(
                b => b.SiteId == site.Id && b.Branch == publishBranch &&
                     (b.Status == SiteBuildStatus.Queued || b.Status == SiteBuildStatus.Building), ct);
            if (inFlight) return;

            var cms = scope.GetRequiredService<CmsDbContext>();
            var events = scope.GetRequiredService<IEventPublisher>();
            var live = scope.GetRequiredService<SiteLiveUpdates>();
            var buildId = await EnqueueBranchBuildAsync(db, cms, events, git, site, live, null, publishBranch, ct);

            scope.GetRequiredService<ILoggerFactory>().CreateLogger(typeof(SiteEndpoints)).LogInformation(
                "{Branch} head {Head} landed while build {Prev} was running; queued catch-up build {Build} for site {Site}.",
                release, SiteLiveUpdates.Short(head), completedBuildId, buildId, site.Id);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            scope.GetRequiredService<ILoggerFactory>().CreateLogger(typeof(SiteEndpoints))
                .LogWarning(ex, "Could not evaluate the release catch-up for site {Site}.", siteId);
        }
    }

    /// <summary>
    /// Whether this tenant records analytics, resolved here and put on the publish
    /// message.
    ///
    /// It is answered by the producer rather than by site-builder because
    /// site-builder cannot answer it: it connects as a least-privilege role that
    /// reaches the `sites` schema only, so its own read of
    /// `plugins.plugin_instances` failed with 42501 on every publish and silently
    /// assumed "enabled" — which meant a site with analytics switched off still
    /// shipped a cookie banner. Admin-api already holds this permission, so nothing
    /// has to be granted to move the question here.
    ///
    /// Cross-tenant callers exist (the git webhook is anonymous and has no ambient
    /// tenant), so the query filter is bypassed and the tenant matched explicitly.
    /// A failure still errs towards asking rather than tracking.
    /// </summary>
    private static async Task<bool> AnalyticsEnabledAsync(CmsDbContext cms, Guid tenantId, CancellationToken ct)
    {
        try
        {
            if (_visitorTrackingPlugins is not { } tracking)
            {
                return true;
            }
            return await cms.PluginInstances.IgnoreTenantFilter().AsNoTracking()
                .AnyAsync(p => p.TenantId == tenantId && tracking.Contains(p.PluginId) && p.Enabled, ct);
        }
        catch
        {
            return true;
        }
    }

    /// <summary>
    /// How long a build may sit in Queued/Building before a later publish declares it dead.
    ///
    /// <para>This has to exceed what a build is actually ALLOWED to take, and at 15 minutes it
    /// did not: <c>ReactAppBuilder</c> permits 10 minutes for the package install and 8 for
    /// the build itself, so a Mode B site on a cold dependency cache could be reaped while it
    /// was still running perfectly well. The result was a build that reported Failed and then
    /// Succeeded — the reaper's row, overwritten minutes later by the builder's own — which
    /// is worse than either outcome alone, because the author had already been told it broke.
    /// Reaping exists for a crashed builder or a lost message, not for a slow one.</para>
    /// </summary>
    private static readonly TimeSpan BuildStaleAfter = TimeSpan.FromMinutes(25);

    /// <summary>Mark builds stuck in Queued/Building past the timeout as Failed, so a crashed
    /// builder or lost message can't permanently block a site's publishing.</summary>
    private static async Task ReapStaleBuildsAsync(SitesDbContext db, Guid siteId, CancellationToken ct)
    {
        var cutoff = DateTimeOffset.UtcNow - BuildStaleAfter;
        var stale = await db.Builds.IgnoreTenantFilter()
            .Where(b => b.SiteId == siteId
                && (b.Status == SiteBuildStatus.Queued || b.Status == SiteBuildStatus.Building)
                && b.CreatedAt < cutoff)
            .ToListAsync(ct);
        foreach (var b in stale)
        {
            b.Status = SiteBuildStatus.Failed;
            b.Error ??= $"Build timed out — no result within {BuildStaleAfter.TotalMinutes:0} minutes.";
            b.CompletedAt = DateTimeOffset.UtcNow;
        }
        if (stale.Count > 0) await db.SaveChangesAsync(ct);
    }

    /// <summary>A site as an IDE workspace: drafts keyed by the site, its live-update group, and
    /// its release left to deployments where its environment says so.</summary>
    private static GitWorkspace Workspace(Site site, SiteLiveUpdates live, IServiceProvider services, ITenantContext tenant) => new(
        site.Id, site.TenantId, site.GitRepoFullName, site.GitDefaultBranch ?? Dcms.AdminApi.Sites.Git.SiteGitService.DefaultBranch,
        (branch, sha, message, author, actor, ct) => AnnounceCommitAsync(live, site, branch, sha, message, author, actor, ct),
        (update, ct) => live.DraftChangedAsync(site.TenantId, update, ct),
        ct => Environments.EnvironmentOwnershipMiddleware.RefuseIfManagedAsync(services, tenant, "sites", site.KeyAcrossEnvironments, ct));

    /// <summary>The effective branch for an IDE/git request: the given branch, else the
    /// site's default branch, else <c>main</c>.</summary>
    private static string ResolveBranch(string? branch, Site site) =>
        string.IsNullOrWhiteSpace(branch)
            ? (site.GitDefaultBranch ?? Dcms.AdminApi.Sites.Git.SiteGitService.DefaultBranch)
            : branch.Trim();

    private sealed record CreateSiteRequest(string Name, string? RenderMode, string? Definition);
}
