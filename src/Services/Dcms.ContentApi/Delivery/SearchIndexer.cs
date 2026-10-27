using Dcms.Shared.Messaging;
using System.Text;
using System.Text.Json;
using Dcms.PluginSdk.Abstractions;
using Dcms.Shared.Contracts.Messaging;
using Dcms.Shared.Data.Cms;
using Dcms.Shared.Data.Search;
using Microsoft.EntityFrameworkCore;
using NATS.Client.JetStream;
using NATS.Client.JetStream.Models;
using SearchDocument = Dcms.Shared.Data.Search.SearchDocument;
using Dcms.Shared.Data.Rls;

using Dcms.Shared.Data;
namespace Dcms.ContentApi.Delivery;

/// <summary>
/// Maintains the sitewide search index. On any content event it projects the item's
/// published version into a search_document (title + searchable text fields, per the
/// plugin's content-type definition), or removes the document when nothing is published.
/// Cross-tenant (no ambient tenant), so ids are explicit and query filters bypassed.
/// </summary>
public sealed class SearchIndexer(
    INatsJSContext jetStream,
    IServiceProvider services,
    IPluginCatalog catalog,
    ILogger<SearchIndexer> logger) : BackgroundService
{
    private const string DurableName = "content-api-search-indexer";

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var consumer = await jetStream.CreateOrUpdateConsumerAsync(
                    Streams.Cms,
                    new ConsumerConfig(DurableName) { AckPolicy = ConsumerConfigAckPolicy.Explicit },
                    stoppingToken);

                await foreach (var msg in consumer.ConsumeAsync<IndexEvent>(cancellationToken: stoppingToken))
                {
                    try
                    {
                        if (msg.Data is { } e)
                        {
                            // In the environment the content was published in (ADR 0028).
                            using var environment = msg.Scope(e.TenantId);
                            await HandleAsync(e, stoppingToken);
                        }
                        await msg.AckAsync(cancellationToken: stoppingToken);
                    }
                    catch (Exception ex)
                    {
                        logger.LogWarning(ex, "Search index update failed; will redeliver.");
                        await msg.NakAsync(cancellationToken: stoppingToken);
                    }
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Search indexer unavailable; retrying in 5s.");
                await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
            }
        }
    }

    /// <summary>
    /// Brings the item's document in line with the item as it is now, whichever event arrived.
    ///
    /// <para>The durable is shared, so with several content-api replicas a publish and the
    /// unpublish after it can be handled at the same moment, in either order. Acting on the event
    /// lets the wrong one win: an unpublish deleting nothing while the publish is still writing,
    /// or a late unpublish deleting an item that has since been republished. So both subjects
    /// re-read the item, under a per-item lock held until the write commits.</para>
    /// </summary>
    private async Task HandleAsync(IndexEvent e, CancellationToken ct)
    {
        // The event's tenant, for the database too: the plugin-instance, item and version lookups
        // below go by id alone, and this is what keeps them inside that tenant (ADR 0015).
        using var rls = RlsScope.Tenant(e.TenantId);
        using var scope = services.CreateScope();
        var cms = scope.ServiceProvider.GetRequiredService<CmsDbContext>();
        var search = scope.ServiceProvider.GetRequiredService<SearchDbContext>();

        await using var tx = await search.Database.BeginTransactionAsync(ct);
        await search.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT pg_advisory_xact_lock(hashtextextended({"search-index:" + e.ContentItemId}, 0))", ct);

        var doc = await search.Documents.IgnoreTenantFilter()
            .FirstOrDefaultAsync(d => d.TenantId == e.TenantId && d.ContentItemId == e.ContentItemId, ct);
        if (await PublishedAsync(cms, e, ct) is not { } published)
        {
            if (doc is not null)
            {
                search.Documents.Remove(doc);
            }
        }
        else
        {
            if (doc is null)
            {
                doc = new SearchDocument { Id = Guid.NewGuid(), TenantId = e.TenantId, ContentItemId = e.ContentItemId };
                search.Documents.Add(doc);
            }
            doc.PluginInstanceId = e.PluginInstanceId;
            doc.ContentType = e.ContentType;
            doc.Title = published.Title;
            doc.Body = published.Body;
            doc.Url = published.Url;
            doc.UpdatedAt = DateTimeOffset.UtcNow;
        }
        await search.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
    }

    /// <summary>What the item's document should hold now, or null when it should have none: not
    /// published (any more), gone, or not a searchable content type.</summary>
    private async Task<(string Title, string Body, string Url)?> PublishedAsync(CmsDbContext cms, IndexEvent e, CancellationToken ct)
    {
        // Resolve the content type via the instance's plugin.
        var instance = await cms.PluginInstances.IgnoreTenantFilter().AsNoTracking()
            .FirstOrDefaultAsync(p => p.Id == e.PluginInstanceId, ct);
        if (instance is null)
        {
            return null;
        }
        var typeDef = catalog.Find(instance.PluginId)?.ContentTypes.FirstOrDefault(t => t.Name == e.ContentType);
        if (typeDef is not { Searchable: true })
        {
            return null; // not a searchable content type
        }

        var item = await cms.ContentItems.IgnoreTenantFilter().AsNoTracking()
            .FirstOrDefaultAsync(c => c.Id == e.ContentItemId, ct);
        if (item?.PublishedVersionId is not { } versionId)
        {
            return null;
        }
        var version = await cms.ContentVersions.IgnoreTenantFilter().AsNoTracking()
            .FirstOrDefaultAsync(v => v.Id == versionId, ct);
        if (version is null)
        {
            return null;
        }

        var (title, body) = Project(version.DataJson, typeDef);
        return (title, body, $"/api/{instance.Slug}/{e.ContentType}/{e.Slug}");
    }

    // Default contributor: title from the slug field (or "title"); body from all
    // text/markdown/rich-text fields concatenated.
    private static (string Title, string Body) Project(string dataJson, ContentTypeDefinition typeDef)
    {
        using var doc = JsonDocument.Parse(dataJson);
        var root = doc.RootElement;

        string Get(string name) => root.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString() ?? string.Empty
            : string.Empty;

        var titleField = typeDef.SlugField ?? "title";
        var title = Get(titleField);
        if (string.IsNullOrEmpty(title))
        {
            title = Get("title");
        }

        var body = new StringBuilder();
        foreach (var field in typeDef.Fields)
        {
            if (field.Type is ContentFieldType.Text or ContentFieldType.Markdown or ContentFieldType.RichText)
            {
                var value = Get(field.Name);
                if (!string.IsNullOrEmpty(value))
                {
                    body.Append(value).Append('\n');
                }
            }
        }
        return (title, body.ToString());
    }

    private sealed record IndexEvent(Guid TenantId, Guid PluginInstanceId, Guid ContentItemId, string ContentType, string Slug);
}
