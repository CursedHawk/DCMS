using System.IO.Compression;
using System.Text;
using System.Text.Json;
using Dcms.Shared.Contracts.Events;
using Dcms.Shared.Contracts.Messaging;
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
/// Consumes site.publish.requested: renders the build's definition snapshot to
/// static HTML (Mode A), uploads the artifacts to dcms-sites, activates the
/// build and emits site.published. Cross-tenant (no ambient tenant), so query
/// filters are bypassed. Resilient to NATS being unavailable.
/// </summary>
public sealed class SitePublishConsumer(
    INatsJSContext jetStream,
    IServiceProvider services,
    IObjectStorage storage,
    IOptions<StorageOptions> storageOptions,
    IEventPublisher events,
    SiteRenderer renderer,
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
        try
        {
            await BuildAsync(job, ct);
            await msg.AckAsync(cancellationToken: ct);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Site build {BuildId} failed", job.BuildId);
            await FailAsync(job, ex.Message, ct);
            await msg.AckAsync(cancellationToken: ct);
        }
    }

    private async Task BuildAsync(SitePublishRequested job, CancellationToken ct)
    {
        using var scope = services.CreateScope();
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
            await BuildReactAppAsync(build.DefinitionSnapshotJson, build.ArtifactPrefix, ct);
        }
        else if (string.Equals(job.RenderMode, SiteRenderMode.StaticFiles.ToString(), StringComparison.OrdinalIgnoreCase))
        {
            await ExtractStaticBundleAsync(build.DefinitionSnapshotJson, build.ArtifactPrefix, ct);
        }
        else
        {
            await PrerenderAsync(build.DefinitionSnapshotJson, build.ArtifactPrefix, ct);
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

    // Mode A: render the component tree to static HTML.
    private async Task PrerenderAsync(string definitionJson, string artifactPrefix, CancellationToken ct)
    {
        var definition = JsonSerializer.Deserialize<SiteDefinition>(definitionJson, JsonOpts) ?? new SiteDefinition();
        foreach (var page in renderer.Render(definition))
        {
            var bytes = Encoding.UTF8.GetBytes(page.Html);
            await using var stream = new MemoryStream(bytes);
            await storage.PutAsync(Bucket, $"{artifactPrefix}/{page.FileName}", stream, bytes.Length, "text/html; charset=utf-8", ct);
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

    // Mode B: materialize the AI/editor file map and run a sandboxed vite build.
    private async Task BuildReactAppAsync(string definitionJson, string artifactPrefix, CancellationToken ct)
    {
        var workDir = Path.Combine(Path.GetTempPath(), $"dcms-react-{Guid.NewGuid():N}");
        Directory.CreateDirectory(workDir);
        try
        {
            var artifacts = await reactBuilder.BuildAsync(definitionJson, workDir, ct);
            foreach (var artifact in artifacts)
            {
                await using var stream = File.OpenRead(artifact.LocalPath);
                await storage.PutAsync(Bucket, $"{artifactPrefix}/{artifact.RelativePath}", stream, stream.Length, artifact.ContentType, ct);
            }
        }
        finally
        {
            try { Directory.Delete(workDir, recursive: true); } catch { /* best effort */ }
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
