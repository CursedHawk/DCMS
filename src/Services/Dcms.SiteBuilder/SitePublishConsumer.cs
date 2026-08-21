using Dcms.Shared.Audit;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using Dcms.Shared.Contracts.Events;
using Dcms.Shared.Contracts.Messaging;
using Dcms.Shared.Data.Cms;
using Dcms.Shared.Data.Sites;
using Dcms.Shared.Messaging;
using Dcms.Shared.Storage;
using Dcms.SiteBuilder.Runtime;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using NATS.Client.JetStream;
using NATS.Client.JetStream.Models;

namespace Dcms.SiteBuilder;

/// <summary>
/// Consumes site.publish.requested: turns the build's definition snapshot into
/// static artifacts (Mode A assembles the builder's HTML/CSS source, Mode B runs
/// the site's own React build, Mode C extracts an uploaded bundle), uploads them
/// to dcms-sites, activates the build and emits site.published. Cross-tenant (no
/// ambient tenant), so query filters are bypassed. Resilient to NATS being
/// unavailable.
/// </summary>
public sealed class SitePublishConsumer(
    INatsJSContext jetStream,
    IServiceProvider services,
    IObjectStorage storage,
    IOptions<StorageOptions> storageOptions,
    IEventPublisher events,
    StaticSiteAssembler assembler,
    ReactAppBuilder reactBuilder,
    ILogger<SitePublishConsumer> logger) : BackgroundService
{
    private const string DurableName = "site-builder";
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

    private string Bucket => storageOptions.Value.SitesBucket;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var consumer = await jetStream.CreateOrUpdateConsumerAsync(
                    Streams.Sites,
                    new ConsumerConfig(DurableName)
                    {
                        FilterSubject = Subjects.SitePublishRequested,
                        AckPolicy = ConsumerConfigAckPolicy.Explicit,
                        MaxAckPending = 2,
                    },
                    stoppingToken);

                await foreach (var msg in consumer.ConsumeAsync<SitePublishRequested>(cancellationToken: stoppingToken))
                {
                    await HandleAsync(msg, stoppingToken);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Site builder unavailable; retrying in 5s.");
                await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
            }
        }
    }

    private async Task HandleAsync(INatsJSMsg<SitePublishRequested> msg, CancellationToken ct)
    {
        var job = msg.Data;
        if (job is null)
        {
            await msg.AckAsync(cancellationToken: ct);
            return;
        }
        // Puts back the person who clicked publish. Their context came through the content
        // outbox row, out of the dispatcher and across JetStream to get here — this is the far
        // end of that chain, and the record below is the reason the chain exists.
        using var serviceScope = services.CreateScope();
        using var context = msg.RestoreAuditContext(serviceScope.ServiceProvider, job.TenantId);
        var audit = serviceScope.ServiceProvider.GetRequiredService<IAuditRecorder>();

        try
        {
            await BuildAsync(job, serviceScope, ct);
            audit.Record(AuditActions.SitePublished)
                .For("site", job.SiteId)
                .With("build_id", job.BuildId)
                .With("render_mode", job.RenderMode);
            await msg.AckAsync(cancellationToken: ct);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Site build {BuildId} failed", job.BuildId);
            await FailAsync(job, ex.Message, ct);
            audit.Record(AuditActions.SiteBuildFailed)
                .For("site", job.SiteId)
                .With("build_id", job.BuildId)
                .Failed(ex.Message);
            await msg.AckAsync(cancellationToken: ct);
        }

        // Nothing else will: this service has no request pipeline, and its records go over
        // JetStream rather than riding a transaction of its own.
        await audit.FlushAsync(ct);
    }

    private async Task BuildAsync(SitePublishRequested job, IServiceScope scope, CancellationToken ct)
    {
        var db = scope.ServiceProvider.GetRequiredService<SitesDbContext>();

        var build = await db.Builds.IgnoreQueryFilters().FirstOrDefaultAsync(b => b.Id == job.BuildId, ct);
        if (build is null)
        {
            logger.LogWarning("Build {BuildId} not found; skipping.", job.BuildId);
            return;
        }
        build.Status = SiteBuildStatus.Building;
        await db.SaveChangesAsync(ct);

        if (string.Equals(job.RenderMode, SiteRenderMode.ReactApp.ToString(), StringComparison.OrdinalIgnoreCase))
        {
            build.LogObjectKey = LogKey(build.ArtifactPrefix);
            await BuildReactAppAsync(build.DefinitionSnapshotJson, build.ArtifactPrefix, ct);
        }
        else if (string.Equals(job.RenderMode, SiteRenderMode.StaticFiles.ToString(), StringComparison.OrdinalIgnoreCase))
        {
            await ExtractStaticBundleAsync(build.DefinitionSnapshotJson, build.ArtifactPrefix, ct);
        }
        else
        {
            await PrerenderAsync(build.DefinitionSnapshotJson, build.ArtifactPrefix, job.AnalyticsEnabled, ct);
        }

        build.Status = SiteBuildStatus.Succeeded;
        build.CompletedAt = DateTimeOffset.UtcNow;

        var site = await db.Sites.IgnoreQueryFilters().FirstOrDefaultAsync(s => s.Id == job.SiteId, ct);
        if (site is not null)
        {
            site.ActiveBuildId = build.Id;
            site.UpdatedAt = DateTimeOffset.UtcNow;
        }
        await db.SaveChangesAsync(ct);

        await events.PublishAsync(Subjects.SitePublished, new SitePublished(
            Guid.NewGuid(), DateTimeOffset.UtcNow, job.TenantId, job.SiteId, build.Id, build.ArtifactPrefix), ct);
    }

    // Mode A: assemble the builder's committed HTML/CSS source into static pages.
    private async Task PrerenderAsync(
        string definitionJson, string artifactPrefix, bool? analyticsEnabled, CancellationToken ct)
    {
        var files = SiteFileMap.Parse(definitionJson);
        if (files.Count == 0)
        {
            throw new InvalidOperationException(
                "This site has no source files to publish. Open it in the builder and save it, then publish again.");
        }

        // Whether the tenant records analytics comes from the message: this service
        // connects as a least-privilege role that reaches the `sites` schema alone,
        // so asking the database itself failed on every publish (42501) and silently
        // fell back to "enabled" — every site got a cookie banner. A message from
        // before the field existed says nothing, which still means enabled: err
        // towards asking rather than tracking.
        var site = assembler.Assemble(files, analyticsEnabled ?? true);

        foreach (var page in site.Pages)
        {
            var bytes = Encoding.UTF8.GetBytes(page.Html);
            await using var stream = new MemoryStream(bytes);
            await storage.PutAsync(Bucket, $"{artifactPrefix}/{page.FileName}", stream, bytes.Length, "text/html; charset=utf-8", ct);
        }

        // Stylesheets and any assets committed alongside them ship verbatim; the
        // pages link to them by their repo path, so the layout is preserved.
        foreach (var file in site.Files)
        {
            await using var stream = new MemoryStream(file.Content);
            await storage.PutAsync(Bucket, $"{artifactPrefix}/{file.FileName}", stream, file.Content.Length, file.ContentType, ct);
        }

        // The pages reference /_dcms/hydrate.js (it populates data-bound/plugin
        // placeholders client-side). Ship the runtime with every Mode A build.
        var hydrate = HydrateRuntime.Bytes;
        await using var hydrateStream = new MemoryStream(hydrate);
        await storage.PutAsync(Bucket, $"{artifactPrefix}/_dcms/hydrate.js", hydrateStream, hydrate.Length, "text/javascript", ct);
    }

    // Mode C: extract the staged, pre-sanitized upload bundle into the build's
    // artifact prefix, one object per file. The snapshot carries the bundle key.
    private async Task ExtractStaticBundleAsync(string snapshotJson, string artifactPrefix, CancellationToken ct)
    {
        using var snapshot = JsonDocument.Parse(snapshotJson);
        if (!snapshot.RootElement.TryGetProperty("bundleKey", out var keyProp) ||
            keyProp.GetString() is not { Length: > 0 } bundleKey)
        {
            throw new InvalidOperationException("Static-files build snapshot is missing a bundle key.");
        }

        byte[] zipBytes;
        await using (var download = await storage.GetAsync(Bucket, bundleKey, ct))
        using (var ms = new MemoryStream())
        {
            await download.CopyToAsync(ms, ct);
            zipBytes = ms.ToArray();
        }

        using var archive = new ZipArchive(new MemoryStream(zipBytes), ZipArchiveMode.Read);
        foreach (var entry in archive.Entries)
        {
            ct.ThrowIfCancellationRequested();
            // The bundle was sanitized at upload, but re-validate defensively.
            var path = StaticSiteFiles.NormalizeEntryPath(entry.FullName);
            if (path is null)
            {
                continue;
            }
            await using var es = entry.Open();
            using var buffer = new MemoryStream();
            await es.CopyToAsync(buffer, ct);
            buffer.Position = 0;
            await storage.PutAsync(Bucket, $"{artifactPrefix}/{path}", buffer, buffer.Length,
                StaticSiteFiles.ContentTypeFor(path), ct);
        }
    }

    // Mode B: materialize the site's project and run its own package-manager
    // install + build. The full step log is stored to object storage (under a
    // deterministic key) whether the build succeeds or fails, so the IDE can show
    // the user exactly why a build failed.
    private async Task BuildReactAppAsync(string definitionJson, string artifactPrefix, CancellationToken ct)
    {
        // Per-build dir lives directly under the work root so the sandbox can bind-mount
        // it by host path (DCMS_BUILD_WORK_HOST_ROOT + leaf). Defaults to the temp dir
        // when sandboxing is off (dev).
        var workRoot = Environment.GetEnvironmentVariable("DCMS_BUILD_WORK_DIR");
        if (string.IsNullOrWhiteSpace(workRoot))
        {
            workRoot = Path.GetTempPath();
        }
        var workDir = Path.Combine(workRoot, $"dcms-react-{Guid.NewGuid():N}");
        Directory.CreateDirectory(workDir);
        // The sandbox container may run as a different uid; let it create node_modules/
        // dist under the (throwaway) work dir. Files it creates use umask 0 (see the
        // sandbox image) so the service can read the artifacts and clean up afterwards.
        if (!OperatingSystem.IsWindows())
        {
            try { File.SetUnixFileMode(workDir, (UnixFileMode)0b111_111_111); } catch { /* best effort */ }
        }
        var log = new StringBuilder();
        try
        {
            var artifacts = await reactBuilder.BuildAsync(definitionJson, workDir, log, ct);
            foreach (var artifact in artifacts)
            {
                await using var stream = File.OpenRead(artifact.LocalPath);
                await storage.PutAsync(Bucket, $"{artifactPrefix}/{artifact.RelativePath}", stream, stream.Length, artifact.ContentType, ct);
            }
            log.AppendLine($"✓ Build succeeded — {artifacts.Count} file(s) in dist/.");
        }
        finally
        {
            await StoreLogAsync(artifactPrefix, log.ToString(), ct);
            try { Directory.Delete(workDir, recursive: true); } catch { /* best effort */ }
        }
    }

    // Deterministic per-build log key so both the success and failure paths (which
    // run in different DB scopes) agree without threading the value between them.
    private static string LogKey(string artifactPrefix) => $"{artifactPrefix}/_dcms-build.log";

    private async Task StoreLogAsync(string artifactPrefix, string log, CancellationToken ct)
    {
        try
        {
            var bytes = Encoding.UTF8.GetBytes(log);
            await using var stream = new MemoryStream(bytes);
            await storage.PutAsync(Bucket, LogKey(artifactPrefix), stream, bytes.Length, "text/plain; charset=utf-8", ct);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed storing build log for {Prefix}", artifactPrefix);
        }
    }

    private async Task FailAsync(SitePublishRequested job, string error, CancellationToken ct)
    {
        try
        {
            using var scope = services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<SitesDbContext>();
            var build = await db.Builds.IgnoreQueryFilters().FirstOrDefaultAsync(b => b.Id == job.BuildId, ct);
            if (build is not null)
            {
                build.Status = SiteBuildStatus.Failed;
                build.Error = error;
                // The build log (if a React build got far enough to write one) lives at
                // a deterministic key; point the build at it so the IDE can show it.
                build.LogObjectKey ??= LogKey(build.ArtifactPrefix);
                build.CompletedAt = DateTimeOffset.UtcNow;
                await db.SaveChangesAsync(ct);
            }
            await events.PublishAsync(Subjects.SiteBuildFailed, new SiteBuildFailed(
                Guid.NewGuid(), DateTimeOffset.UtcNow, job.TenantId, job.SiteId, job.BuildId, error), ct);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed recording site build failure for {BuildId}", job.BuildId);
        }
    }
}
