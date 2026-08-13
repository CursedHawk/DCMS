using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Dcms.AdminApi.Tenancy;
using Dcms.Shared.Contracts.Events;
using Dcms.Shared.Contracts.Messaging;
using Dcms.Shared.Data.Sites;
using Dcms.Shared.Data.Tenancy;
using Dcms.Shared.Kernel.Abstractions;
using Dcms.Shared.Messaging;
using Dcms.Shared.Security;
using Dcms.Shared.Storage;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Dcms.AdminApi.Sites;

public static class SiteEndpoints
{
    public static IEndpointRouteBuilder MapSiteEndpoints(this IEndpointRouteBuilder app)
    {
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
                DraftDefinitionJson = body.Definition ?? "{\"version\":1,\"theme\":{},\"pages\":[],\"nav\":[]}",
            };
            db.Sites.Add(site);
            await db.SaveChangesAsync(ct);
            return Results.Created($"/api/admin/sites/{site.Id}", new { id = site.Id });
        }).RequirePermission(PlatformPermissions.SiteEdit);

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
        }).RequirePermission(PlatformPermissions.SiteEdit).DisableAntiforgery();

        // Build history for a site (status, source commit, log availability, which one is
        // live) — drives both the rollback UI and the IDE Deployments panel.
        app.MapGet("/api/admin/sites/{id:guid}/builds", async (Guid id, int? limit, SitesDbContext db, CancellationToken ct) =>
        {
            var site = await db.Sites.FirstOrDefaultAsync(s => s.Id == id, ct);
            if (site is null)
            {
                return Results.NotFound();
            }
            var take = Math.Clamp(limit ?? 30, 1, 100);
            var builds = await db.Builds
                .Where(b => b.SiteId == id)
                .OrderByDescending(b => b.CreatedAt)
                .Take(take)
                .Select(b => new
                {
                    id = b.Id,
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
                b.status,
                b.gitCommitSha,
                shortSha = b.gitCommitSha is { Length: >= 7 } ? b.gitCommitSha[..7] : b.gitCommitSha,
                b.error,
                b.hasLog,
                b.createdAt,
                b.completedAt,
                // `active` for the IDE panel; `isActive` kept for the existing rollback UI.
                active = site.ActiveBuildId == b.id,
                isActive = site.ActiveBuildId == b.id,
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
            site.ActiveBuildId = build.Id;
            site.UpdatedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(ct);
            return Results.NoContent();
        }).RequirePermission(PlatformPermissions.SitePublish);

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
        }).RequirePermission(PlatformPermissions.SiteEdit);

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
            Dcms.AdminApi.Sites.Git.SiteGitService git, CancellationToken ct) =>
        {
            var site = await db.Sites.FirstOrDefaultAsync(s => s.Id == id, ct);
            if (site is null) return Results.NotFound();
            if (site.RenderMode != SiteRenderMode.ReactApp)
                return Results.BadRequest(new { error = "The web IDE is only for ReactApp (Mode B) sites." });

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

            var userId = user.UserId ?? Guid.Empty;
            var b = ResolveBranch(branch, site);
            var draft = await db.Drafts.FirstOrDefaultAsync(
                d => d.SiteId == id && d.UserId == userId && d.Branch == b, ct);

            if (draft is null)
            {
                // Seed from git HEAD (or the legacy DB draft if the repo isn't ready).
                var headSha = site.GitRepoFullName is not null
                    ? await git.HeadShaAsync(site.GitRepoFullName, b, ct) : null;
                var seeded = site.GitRepoFullName is not null && headSha is not null
                    ? await git.ReadFilesAsync(site.GitRepoFullName, b, ct)
                    : SiteFileMap.Parse(site.DraftDefinitionJson);
                // A freshly auto-initialised repo has only the placeholder README; treat
                // that as empty so the IDE seeds its React starter instead of showing it.
                if (seeded.Count == 1 && seeded.ContainsKey("README.md")) seeded.Clear();
                draft = new SiteDraft
                {
                    Id = Guid.NewGuid(),
                    TenantId = tenant.TenantId ?? Guid.Empty,
                    SiteId = id,
                    UserId = userId,
                    Branch = b,
                    DefinitionJson = SiteFileMap.Serialize(seeded),
                    BaseSha = headSha,
                    Version = 0,
                    UpdatedAt = DateTimeOffset.UtcNow,
                };
                db.Drafts.Add(draft);
                await db.SaveChangesAsync(ct);
            }

            var files = SiteFileMap.Parse(draft.DefinitionJson);
            return Results.Ok(new
            {
                branch = b,
                baseSha = draft.BaseSha,
                version = draft.Version,
                files,
                hashes = SiteFileMap.HashAll(files),
            });
        }).RequirePermission(PlatformPermissions.SiteEdit);

        // Granular per-file save into the current user's working draft for a branch.
        // Merges only the named files into the stored draft; each entry carries the
        // hash the client last saw, so the same account editing the same file+branch
        // from two tabs is reported as a conflict (409) instead of clobbering. Edits
        // to different files just merge. No git write happens here — that's on commit.
        app.MapPatch("/api/admin/sites/{id:guid}/ide/files", async (
            Guid id, string? branch, SaveFilesRequest body,
            SitesDbContext db, ITenantContext tenant, CurrentUser user, CancellationToken ct) =>
        {
            var site = await db.Sites.FirstOrDefaultAsync(s => s.Id == id, ct);
            if (site is null) return Results.NotFound();

            var userId = user.UserId ?? Guid.Empty;
            var b = ResolveBranch(branch, site);
            var draft = await db.Drafts.FirstOrDefaultAsync(
                d => d.SiteId == id && d.UserId == userId && d.Branch == b, ct);
            if (draft is null)
                return Results.BadRequest(new { error = "Open the branch before saving." });

            var put = body.Put ?? new Dictionary<string, FileWrite>();
            var del = body.Delete ?? [];

            foreach (var path in put.Keys.Concat(del.Select(d => d.Path)))
            {
                // A Mode B site is a complete, self-contained front-end project and owns
                // its own package.json + lockfile, so nothing is off-limits here beyond
                // path-safety (the build honors whatever the site commits).
                if (!SiteFileMap.IsSafePath(path))
                    return Results.BadRequest(new { error = $"Illegal path in file map: {path}" });
            }

            var files = SiteFileMap.Parse(draft.DefinitionJson);

            var conflicts = new List<object>();
            foreach (var (path, write) in put)
            {
                var current = files.TryGetValue(path, out var c) ? SiteFileMap.Hash(c) : null;
                if (write.BaseHash != current)
                    conflicts.Add(new { path, current = files.GetValueOrDefault(path) });
            }
            foreach (var d in del)
            {
                var current = files.TryGetValue(d.Path, out var c) ? SiteFileMap.Hash(c) : null;
                if (d.BaseHash != current)
                    conflicts.Add(new { path = d.Path, current = files.GetValueOrDefault(d.Path) });
            }
            if (conflicts.Count > 0)
                return Results.Json(
                    new { error = "conflict", version = draft.Version, conflicts },
                    statusCode: StatusCodes.Status409Conflict);

            foreach (var (path, write) in put) files[path] = write.Content;
            foreach (var d in del) files.Remove(d.Path);

            draft.DefinitionJson = SiteFileMap.Serialize(files);
            draft.Version++;
            draft.UpdatedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(ct);

            return Results.Ok(new
            {
                version = draft.Version,
                hashes = put.ToDictionary(kv => kv.Key, kv => SiteFileMap.Hash(kv.Value.Content)),
            });
        }).RequirePermission(PlatformPermissions.SiteEdit);

        app.MapPost("/api/admin/sites/{id:guid}/publish", async (
            Guid id, string? branch, SitesDbContext db, ITenantContext tenant, IEventPublisher events,
            CurrentUser user, Dcms.AdminApi.Sites.Git.SiteGitService git, CancellationToken ct) =>
        {
            var site = await db.Sites.FirstOrDefaultAsync(s => s.Id == id, ct);
            if (site is null)
            {
                return Results.NotFound();
            }

            // Mode B (git-backed): release is a normal branch whose only special power is
            // that a push to it builds+deploys. Publishing = merge the current branch into
            // release. The frontend commits the working draft first, so this ships committed
            // work. Publishing while already on release just (re)builds the release head.
            if (site.RenderMode == SiteRenderMode.ReactApp && site.GitRepoFullName is not null && git.Enabled)
            {
                var release = Dcms.AdminApi.Sites.Git.SiteGitService.ReleaseBranch;
                var sourceBranch = ResolveBranch(branch, site);
                await git.EnsureReleaseBranchAsync(site.GitRepoFullName, ct);
                try { await git.EnsureWebhookAsync(site.GitRepoFullName, ct); } catch { /* best effort */ }

                if (sourceBranch == release)
                {
                    // Already on release: deploy the current release head (force a fresh build
                    // even if the tree is unchanged — this is the "redeploy" affordance).
                    var buildId = await EnqueueReleaseBuildAsync(db, events, git, site, ct);
                    return Results.Accepted($"/api/admin/sites/{site.Id}/builds/{buildId}", new { released = true, buildId });
                }

                var outcome = await git.MergeAsync(site.GitRepoFullName, release, sourceBranch, ct);
                if (outcome.UpToDate)
                    return Results.Ok(new { released = false, upToDate = true });
                if (outcome.Merged)
                    return Results.Accepted($"/api/admin/sites/{site.Id}", new { released = true, sha = outcome.Sha, branch = release });
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

            await events.PublishAsync(Subjects.SitePublishRequested, new SitePublishRequested(
                Guid.NewGuid(), DateTimeOffset.UtcNow, tenantId, site.Id, build.Id, site.RenderMode.ToString()), ct);

            return Results.Accepted($"/api/admin/sites/{site.Id}/builds/{build.Id}", new { buildId = build.Id });
        }).RequirePermission(PlatformPermissions.SitePublish);

        // Link a verified domain to a site so site-host serves it there.
        app.MapPost("/api/admin/domains/{id:guid}/site", async (
            Guid id, LinkSiteRequest body, TenancyDbContext db, CancellationToken ct) =>
        {
            var domain = await db.Domains.FirstOrDefaultAsync(d => d.Id == id, ct);
            if (domain is null)
            {
                return Results.NotFound();
            }
            domain.SiteId = body.SiteId;
            await db.SaveChangesAsync(ct);
            return Results.NoContent();
        }).RequirePermission(PlatformPermissions.DomainsManage);

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
            if (site.RenderMode != SiteRenderMode.ReactApp)
                return Results.BadRequest(new { error = "Git source is only available for ReactApp (Mode B) sites." });

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
        }).RequirePermission(PlatformPermissions.SiteEdit);

        // Git status for a Mode B site. Auto-provisions the repo on first open so the
        // IDE never needs a manual provision step. Returns clone URLs for the panel.
        app.MapGet("/api/admin/sites/{id:guid}/git", async (
            Guid id, SitesDbContext db, ITenantContext tenant, Dcms.AdminApi.Sites.Git.SiteGitService git,
            CurrentUser user, Dcms.AdminApi.Sites.Git.RepoAccessReconciler repoAccess, CancellationToken ct) =>
        {
            var site = await db.Sites.FirstOrDefaultAsync(s => s.Id == id, ct);
            if (site is null) return Results.NotFound();
            if (site.RenderMode != SiteRenderMode.ReactApp || !git.Enabled)
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
            Guid id, SitesDbContext db, Dcms.AdminApi.Sites.Git.SiteGitService git, CancellationToken ct) =>
        {
            var site = await db.Sites.FirstOrDefaultAsync(s => s.Id == id, ct);
            if (site?.GitRepoFullName is null) return Results.Ok(Array.Empty<object>());
            var branches = await git.BranchesAsync(site.GitRepoFullName, ct);
            return Results.Ok(branches.Select(b => new { name = b.Name, sha = b.Commit?.Id }));
        }).RequirePermission(PlatformPermissions.SiteEdit);

        app.MapGet("/api/admin/sites/{id:guid}/git/history", async (
            Guid id, string? branch, int? limit, SitesDbContext db,
            Dcms.AdminApi.Sites.Git.SiteGitService git, CancellationToken ct) =>
        {
            var site = await db.Sites.FirstOrDefaultAsync(s => s.Id == id, ct);
            if (site?.GitRepoFullName is null) return Results.Ok(Array.Empty<object>());
            var b = branch ?? site.GitDefaultBranch ?? Dcms.AdminApi.Sites.Git.SiteGitService.DefaultBranch;
            var commits = await git.HistoryAsync(site.GitRepoFullName, b, Math.Clamp(limit ?? 50, 1, 100), ct);
            return Results.Ok(commits.Select(c => new
            {
                sha = c.Sha,
                shortSha = c.Sha.Length >= 7 ? c.Sha[..7] : c.Sha,
                message = c.Commit?.Message,
                author = c.Commit?.Author?.Name,
                date = c.Commit?.Author?.Date,
                htmlUrl = c.HtmlUrl,
                avatar = c.Author?.AvatarUrl,
            }));
        }).RequirePermission(PlatformPermissions.SiteEdit);

        // Source-control "changes": the current user's working draft on a branch vs that
        // branch's git HEAD. Drives the changed-files list and the per-file diff view.
        // Content is included (both sides) so the frontend Monaco diff needs no extra
        // round-trip; oversized/binary files return null content (marked truncated).
        app.MapGet("/api/admin/sites/{id:guid}/git/changes", async (
            Guid id, string? branch, SitesDbContext db, CurrentUser user,
            Dcms.AdminApi.Sites.Git.SiteGitService git, CancellationToken ct) =>
        {
            var site = await db.Sites.FirstOrDefaultAsync(s => s.Id == id, ct);
            if (site?.GitRepoFullName is null || !git.Enabled) return Results.Ok(Array.Empty<object>());

            var userId = user.UserId ?? Guid.Empty;
            var b = ResolveBranch(branch, site);
            var draft = await db.Drafts.FirstOrDefaultAsync(
                d => d.SiteId == id && d.UserId == userId && d.Branch == b, ct);
            var draftFiles = draft is null
                ? new Dictionary<string, string>()
                : SiteFileMap.Parse(draft.DefinitionJson);
            var headFiles = await git.ReadFilesAsync(site.GitRepoFullName, b, ct);

            const int maxDiffBytes = 512 * 1024;
            static string? Cap(string? s) => s is not null && s.Length <= maxDiffBytes ? s : null;

            var changes = new List<object>();
            foreach (var path in draftFiles.Keys.Union(headFiles.Keys).OrderBy(p => p, StringComparer.Ordinal))
            {
                var inHead = headFiles.TryGetValue(path, out var headContent);
                var inDraft = draftFiles.TryGetValue(path, out var draftContent);
                string status;
                if (inDraft && !inHead) status = "added";
                else if (!inDraft && inHead) status = "deleted";
                else if (headContent != draftContent) status = "modified";
                else continue; // unchanged
                changes.Add(new
                {
                    path,
                    status,
                    headContent = Cap(headContent),
                    draftContent = Cap(draftContent),
                    truncated = (inHead && headContent!.Length > maxDiffBytes)
                                || (inDraft && draftContent!.Length > maxDiffBytes),
                });
            }
            return Results.Ok(changes);
        }).RequirePermission(PlatformPermissions.SiteEdit);

        // Commit the current user's working draft (on `branch`) to git — to that same
        // branch, an existing `targetBranch`, or a freshly created `newBranch`. The user
        // is the git author; the platform is the committer. A commit to `release` fires
        // the push webhook → build+deploy. Committing to the SAME branch after it moved
        // underneath the draft returns 409 `branch-moved` (reconcile/reload; full merge
        // is planned).
        app.MapPost("/api/admin/sites/{id:guid}/git/commit", async (
            Guid id, CommitRequest body, SitesDbContext db, ITenantContext tenant, CurrentUser user,
            Dcms.AdminApi.Sites.Git.SiteGitService git, CancellationToken ct) =>
        {
            if (!git.Enabled) return Results.Problem("Git backend is not configured.", statusCode: 503);
            if (string.IsNullOrWhiteSpace(body.Message))
                return Results.BadRequest(new { error = "A commit message is required." });

            var site = await db.Sites.FirstOrDefaultAsync(s => s.Id == id, ct);
            if (site is null) return Results.NotFound();
            if (site.GitRepoFullName is null)
                return Results.BadRequest(new { error = "This site has no git repository." });

            var userId = user.UserId ?? Guid.Empty;
            var sourceBranch = ResolveBranch(body.Branch, site);
            var draft = await db.Drafts.FirstOrDefaultAsync(
                d => d.SiteId == id && d.UserId == userId && d.Branch == sourceBranch, ct);
            if (draft is null) return Results.BadRequest(new { error = "Nothing to commit — open the branch first." });

            // Resolve the target branch (create it off the source branch if requested).
            string target;
            if (!string.IsNullOrWhiteSpace(body.NewBranch))
            {
                target = body.NewBranch!.Trim();
                await git.CreateBranchAsync(site.GitRepoFullName, target, sourceBranch, ct);
            }
            else
            {
                target = string.IsNullOrWhiteSpace(body.TargetBranch) ? sourceBranch : body.TargetBranch!.Trim();
            }

            var draftFiles = SiteFileMap.Parse(draft.DefinitionJson);
            IReadOnlyDictionary<string, string> toCommit = draftFiles;

            // Divergence handling only applies when writing back to the branch we based on.
            // If the branch moved under the draft (a concurrent commit, external push, …),
            // replay the user's edits onto the new HEAD at file granularity: a file changed
            // on both sides is the only real conflict. When those exist, the client must say
            // how to resolve them (`resolve` = "mine" keeps the draft's version, "theirs"
            // keeps the branch's) — otherwise we report them so it can ask.
            if (target == sourceBranch && draft.BaseSha is not null)
            {
                var head = await git.HeadShaAsync(site.GitRepoFullName, target, ct);
                if (head is not null && head != draft.BaseSha)
                {
                    var reconcile = await git.ReconcileAsync(site.GitRepoFullName, target, draft.BaseSha, draftFiles, ct);
                    if (reconcile.Conflicts.Count > 0)
                    {
                        var resolve = body.Resolve?.Trim().ToLowerInvariant();
                        if (resolve is not ("mine" or "theirs"))
                            return Results.Json(
                                new
                                {
                                    error = "branch-moved",
                                    head,
                                    files = reconcile.Conflicts.Select(f => new
                                    {
                                        path = f.Path,
                                        // ReleaseContent = the branch's current version; BranchContent = your edit.
                                        branchContent = f.ReleaseContent,
                                        draftContent = f.BranchContent,
                                        baseContent = f.BaseContent,
                                    }),
                                },
                                statusCode: StatusCodes.Status409Conflict);

                        // "theirs" is already reflected in reconcile.Merged (it holds HEAD's
                        // version for conflicting files); "mine" overlays the draft's version.
                        var merged = new Dictionary<string, string>(reconcile.Merged, StringComparer.Ordinal);
                        if (resolve == "mine")
                            foreach (var f in reconcile.Conflicts)
                            {
                                if (f.BranchContent is null) merged.Remove(f.Path);
                                else merged[f.Path] = f.BranchContent;
                            }
                        toCommit = merged;
                    }
                    else
                    {
                        toCommit = reconcile.Merged;
                    }
                }
            }

            var message = string.IsNullOrWhiteSpace(body.Description)
                ? body.Message!.Trim()
                : body.Message!.Trim() + "\n\n" + body.Description!.Trim();
            var sha = await git.SyncAsync(site.GitRepoFullName, target, toCommit, message, user.Name, user.Email, ct);

            if (target == sourceBranch)
            {
                // Draft content now equals the new HEAD (including anything a reconcile
                // pulled in) — persist the merged map and mark it clean at that sha.
                draft.DefinitionJson = SiteFileMap.Serialize(toCommit);
                draft.BaseSha = sha;
            }
            else
            {
                // Work moved onto `target`; the source branch reverts to its (unchanged)
                // HEAD so its draft is clean again. The frontend switches to `target`.
                var srcHead = await git.HeadShaAsync(site.GitRepoFullName, sourceBranch, ct);
                draft.DefinitionJson = SiteFileMap.Serialize(
                    await git.ReadFilesAsync(site.GitRepoFullName, sourceBranch, ct));
                draft.BaseSha = srcHead;
            }
            draft.UpdatedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(ct);

            return Results.Ok(new { sha, branch = target });
        }).RequirePermission(PlatformPermissions.SiteEdit);

        // Create a branch off another (defaults to the site's default branch).
        app.MapPost("/api/admin/sites/{id:guid}/git/branches", async (
            Guid id, CreateBranchRequest body, SitesDbContext db,
            Dcms.AdminApi.Sites.Git.SiteGitService git, CancellationToken ct) =>
        {
            if (!git.Enabled) return Results.Problem("Git backend is not configured.", statusCode: 503);
            if (string.IsNullOrWhiteSpace(body.Name))
                return Results.BadRequest(new { error = "A branch name is required." });
            var site = await db.Sites.FirstOrDefaultAsync(s => s.Id == id, ct);
            if (site?.GitRepoFullName is null)
                return Results.BadRequest(new { error = "This site has no git repository." });
            var from = ResolveBranch(body.From, site);
            await git.CreateBranchAsync(site.GitRepoFullName, body.Name!.Trim(), from, ct);
            return Results.Ok(new { name = body.Name!.Trim(), from });
        }).RequirePermission(PlatformPermissions.SiteEdit);

        // Pre-merge review: what merging `head` into the release branch would bring
        // (files changed + commit count).
        app.MapGet("/api/admin/sites/{id:guid}/git/compare", async (
            Guid id, string head, string? @base, SitesDbContext db,
            Dcms.AdminApi.Sites.Git.SiteGitService git, CancellationToken ct) =>
        {
            var site = await db.Sites.FirstOrDefaultAsync(s => s.Id == id, ct);
            if (site?.GitRepoFullName is null || !git.Enabled || string.IsNullOrWhiteSpace(head))
                return Results.Ok(new { totalCommits = 0, files = Array.Empty<object>() });
            var b = string.IsNullOrWhiteSpace(@base)
                ? Dcms.AdminApi.Sites.Git.SiteGitService.ReleaseBranch : @base!.Trim();
            var cmp = await git.CompareAsync(site.GitRepoFullName, b, head.Trim(), ct);
            return Results.Ok(new
            {
                totalCommits = cmp.TotalCommits,
                files = (cmp.Files ?? []).Select(f => new { path = f.Filename, status = f.Status }),
            });
        }).RequirePermission(PlatformPermissions.SiteEdit);

        // Merge `head` into `base` (defaults to release). A real 3-way merge: clean changes
        // auto-merge, only true conflicts need resolution. A clean merge into release fires a
        // build. Returns { merged, sha } | { upToDate } | { conflict, files:[{ path,
        // releaseContent (base/ours), branchContent (incoming/theirs), baseContent }] }.
        app.MapPost("/api/admin/sites/{id:guid}/git/merge", async (
            Guid id, MergeRequest body, SitesDbContext db,
            Dcms.AdminApi.Sites.Git.SiteGitService git, CancellationToken ct) =>
        {
            if (!git.Enabled) return Results.Problem("Git backend is not configured.", statusCode: 503);
            if (string.IsNullOrWhiteSpace(body.Head))
                return Results.BadRequest(new { error = "A source branch is required." });
            var site = await db.Sites.FirstOrDefaultAsync(s => s.Id == id, ct);
            if (site?.GitRepoFullName is null)
                return Results.BadRequest(new { error = "This site has no git repository." });

            var @base = string.IsNullOrWhiteSpace(body.Base)
                ? Dcms.AdminApi.Sites.Git.SiteGitService.ReleaseBranch : body.Base!.Trim();
            // Re-assert the build webhook when shipping to release (recovers a repo whose
            // initial hook registration failed — otherwise the merge would never build).
            if (@base == Dcms.AdminApi.Sites.Git.SiteGitService.ReleaseBranch)
                try { await git.EnsureWebhookAsync(site.GitRepoFullName, ct); } catch { /* best effort */ }

            var outcome = await git.MergeAsync(site.GitRepoFullName, @base, body.Head!.Trim(), ct);
            if (outcome.Merged || outcome.UpToDate)
                return Results.Ok(new { merged = outcome.Merged, upToDate = outcome.UpToDate, sha = outcome.Sha });
            return Results.Ok(new
            {
                merged = false,
                conflict = true,
                files = outcome.Conflicts.Select(f => new
                {
                    path = f.Path,
                    releaseContent = f.ReleaseContent,
                    branchContent = f.BranchContent,
                    baseContent = f.BaseContent,
                }),
            });
        }).RequirePermission(PlatformPermissions.SitePublish);

        // Complete a conflicted merge of `head` into `base` (defaults to release): apply the
        // caller's per-file resolutions onto the auto-merged tree and commit the two-parent
        // merge (→ build when base is release). A null resolution value deletes the file.
        app.MapPost("/api/admin/sites/{id:guid}/git/merge/resolve", async (
            Guid id, ResolveMergeRequest body, SitesDbContext db, CurrentUser user,
            Dcms.AdminApi.Sites.Git.SiteGitService git, CancellationToken ct) =>
        {
            if (!git.Enabled) return Results.Problem("Git backend is not configured.", statusCode: 503);
            if (string.IsNullOrWhiteSpace(body.Head))
                return Results.BadRequest(new { error = "A source branch is required." });
            var site = await db.Sites.FirstOrDefaultAsync(s => s.Id == id, ct);
            if (site?.GitRepoFullName is null)
                return Results.BadRequest(new { error = "This site has no git repository." });

            var @base = string.IsNullOrWhiteSpace(body.Base)
                ? Dcms.AdminApi.Sites.Git.SiteGitService.ReleaseBranch : body.Base!.Trim();
            var resolutions = body.Resolutions ?? new Dictionary<string, string?>();
            var sha = await git.ResolveMergeAsync(
                site.GitRepoFullName, @base, body.Head!.Trim(), resolutions, user.Name, user.Email, ct);
            return Results.Ok(new { merged = true, sha });
        }).RequirePermission(PlatformPermissions.SitePublish);

        // Restore an earlier commit's tree into the current user's working draft on a
        // branch (does not commit — the user reviews the diff and commits explicitly).
        app.MapPost("/api/admin/sites/{id:guid}/git/restore", async (
            Guid id, GitRestoreRequest body, string? branch, SitesDbContext db, ITenantContext tenant,
            CurrentUser user, Dcms.AdminApi.Sites.Git.SiteGitService git, CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(body.Sha))
                return Results.BadRequest(new { error = "A commit sha is required." });
            var site = await db.Sites.FirstOrDefaultAsync(s => s.Id == id, ct);
            if (site is null) return Results.NotFound();
            if (site.GitRepoFullName is null || !git.Enabled)
                return Results.BadRequest(new { error = "This site has no git repository." });

            var userId = user.UserId ?? Guid.Empty;
            var b = ResolveBranch(branch, site);
            var files = await git.ReadFilesAsync(site.GitRepoFullName, body.Sha, ct);

            var draft = await db.Drafts.FirstOrDefaultAsync(
                d => d.SiteId == id && d.UserId == userId && d.Branch == b, ct);
            if (draft is null)
            {
                draft = new SiteDraft
                {
                    Id = Guid.NewGuid(),
                    TenantId = tenant.TenantId ?? Guid.Empty,
                    SiteId = id,
                    UserId = userId,
                    Branch = b,
                    BaseSha = await git.HeadShaAsync(site.GitRepoFullName, b, ct),
                };
                db.Drafts.Add(draft);
            }
            draft.DefinitionJson = SiteFileMap.Serialize(files);
            draft.Version++;
            draft.UpdatedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(ct);

            return Results.Ok(new
            {
                branch = b,
                version = draft.Version,
                files,
                hashes = SiteFileMap.HashAll(files),
            });
        }).RequirePermission(PlatformPermissions.SiteEdit);

        // Forgejo push webhook: builds+deploys the site on a push/merge to the RELEASE
        // branch — this is what "publish" lands on. HMAC-verified (no user auth). Cross-
        // tenant — Forgejo doesn't know tenants, so the site is resolved by repo name
        // (owner role bypasses RLS). Any push to `release` builds (app commit, merge, or
        // external `git push`); pushes to feature/dev branches never build.
        app.MapPost("/api/internal/git/webhook", async (
            HttpRequest request, SitesDbContext db, IEventPublisher events,
            Dcms.AdminApi.Sites.Git.SiteGitService git,
            IOptions<Dcms.AdminApi.Sites.Git.ForgejoOptions> gitOptions,
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
                return Results.Unauthorized();
            }

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

            var site = await db.Sites.IgnoreQueryFilters()
                .FirstOrDefaultAsync(s => s.GitRepoFullName == repoFull, ct);
            if (site is null) return Results.Ok(new { ignored = "unknown repo" });

            // Only pushes to the release (production) branch build+deploy.
            var branch = Dcms.AdminApi.Sites.Git.SiteGitService.ReleaseBranch;
            if (gitRef != $"refs/heads/{branch}") return Results.Ok(new { ignored = "not release branch" });

            // Fail builds wedged in Queued/Building past the timeout so a crashed builder
            // can't permanently block this site (the coalescing check below would skip
            // forever otherwise).
            await ReapStaleBuildsAsync(db, site.Id, ct);

            // Coalesce: never stack builds for the same site.
            var inFlight = await db.Builds.IgnoreQueryFilters().AnyAsync(
                b => b.SiteId == site.Id &&
                     (b.Status == SiteBuildStatus.Queued || b.Status == SiteBuildStatus.Building), ct);
            if (inFlight) return Results.Ok(new { skipped = "build in flight" });

            var files = await git.ReadFilesAsync(repoFull, afterSha ?? branch, ct);
            var build = new SiteBuild
            {
                Id = Guid.NewGuid(),
                TenantId = site.TenantId,
                SiteId = site.Id,
                Status = SiteBuildStatus.Queued,
                DefinitionSnapshotJson = SiteFileMap.Serialize(files),
                GitCommitSha = afterSha,
            };
            build.ArtifactPrefix = $"{site.TenantId}/{site.Id}/{build.Id}";
            db.Builds.Add(build);
            await db.SaveChangesAsync(ct);

            await events.PublishAsync(Subjects.SitePublishRequested, new SitePublishRequested(
                Guid.NewGuid(), DateTimeOffset.UtcNow, site.TenantId, site.Id, build.Id, site.RenderMode.ToString()), ct);

            log.LogInformation("Queued build {BuildId} from git push {Sha} to {Repo}", build.Id, afterSha, repoFull);
            return Results.Ok(new { buildId = build.Id });
        }).AllowAnonymous();

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
            Guid id, SitesDbContext db, IEventPublisher events,
            Dcms.AdminApi.Sites.Git.SiteGitService git, CancellationToken ct) =>
        {
            var site = await db.Sites.FirstOrDefaultAsync(s => s.Id == id, ct);
            if (site is null) return Results.NotFound();
            if (site.RenderMode != SiteRenderMode.ReactApp || site.GitRepoFullName is null || !git.Enabled)
                return Results.BadRequest(new { error = "This site has no git-backed release to rebuild." });
            var buildId = await EnqueueReleaseBuildAsync(db, events, git, site, ct);
            return Results.Accepted($"/api/admin/sites/{site.Id}/builds/{buildId}", new { buildId });
        }).RequirePermission(PlatformPermissions.SitePublish);

        return app;
    }

    /// <summary>Queue a build of the current <c>release</c> tree and request it. Reaps any
    /// stale in-flight builds first so a wedged queue can't block the new one.</summary>
    private static async Task<Guid> EnqueueReleaseBuildAsync(
        SitesDbContext db, IEventPublisher events, Dcms.AdminApi.Sites.Git.SiteGitService git,
        Site site, CancellationToken ct)
    {
        await ReapStaleBuildsAsync(db, site.Id, ct);
        var release = Dcms.AdminApi.Sites.Git.SiteGitService.ReleaseBranch;
        var head = await git.HeadShaAsync(site.GitRepoFullName!, release, ct);
        var files = await git.ReadFilesAsync(site.GitRepoFullName!, release, ct);
        var build = new SiteBuild
        {
            Id = Guid.NewGuid(),
            TenantId = site.TenantId,
            SiteId = site.Id,
            Status = SiteBuildStatus.Queued,
            DefinitionSnapshotJson = SiteFileMap.Serialize(files),
            GitCommitSha = head,
        };
        build.ArtifactPrefix = $"{site.TenantId}/{site.Id}/{build.Id}";
        db.Builds.Add(build);
        await db.SaveChangesAsync(ct);
        await events.PublishAsync(Subjects.SitePublishRequested, new SitePublishRequested(
            Guid.NewGuid(), DateTimeOffset.UtcNow, site.TenantId, site.Id, build.Id, site.RenderMode.ToString()), ct);
        return build.Id;
    }

    /// <summary>Mark builds stuck in Queued/Building past the timeout as Failed, so a crashed
    /// builder or lost message can't permanently block a site's publishing.</summary>
    private static async Task ReapStaleBuildsAsync(SitesDbContext db, Guid siteId, CancellationToken ct)
    {
        var cutoff = DateTimeOffset.UtcNow - TimeSpan.FromMinutes(15);
        var stale = await db.Builds.IgnoreQueryFilters()
            .Where(b => b.SiteId == siteId
                && (b.Status == SiteBuildStatus.Queued || b.Status == SiteBuildStatus.Building)
                && b.CreatedAt < cutoff)
            .ToListAsync(ct);
        foreach (var b in stale)
        {
            b.Status = SiteBuildStatus.Failed;
            b.Error ??= "Build timed out — no result within 15 minutes.";
            b.CompletedAt = DateTimeOffset.UtcNow;
        }
        if (stale.Count > 0) await db.SaveChangesAsync(ct);
    }

    /// <summary>The effective branch for an IDE/git request: the given branch, else the
    /// site's default branch, else <c>main</c>.</summary>
    private static string ResolveBranch(string? branch, Site site) =>
        string.IsNullOrWhiteSpace(branch)
            ? (site.GitDefaultBranch ?? Dcms.AdminApi.Sites.Git.SiteGitService.DefaultBranch)
            : branch.Trim();

    private sealed record CreateSiteRequest(string Name, string? RenderMode, string? Definition);
    private sealed record LinkSiteRequest(Guid SiteId);
    private sealed record SaveFilesRequest(int? BaseVersion, Dictionary<string, FileWrite>? Put, DeleteEntry[]? Delete);
    private sealed record FileWrite(string Content, string? BaseHash);
    private sealed record DeleteEntry(string Path, string? BaseHash);
    private sealed record GitRestoreRequest(string Sha);
    private sealed record CommitRequest(string? Branch, string? TargetBranch, string? NewBranch, string Message, string? Description, string? Resolve);
    private sealed record CreateBranchRequest(string Name, string? From);
    private sealed record MergeRequest(string Head, string? Base, string? Strategy);
    private sealed record ResolveMergeRequest(string Head, string? Base, Dictionary<string, string?>? Resolutions);
}
