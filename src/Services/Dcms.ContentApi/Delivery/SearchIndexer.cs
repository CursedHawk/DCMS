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

namespace Dcms.ContentApi.Delivery;

/// <summary>
/// Maintains the sitewide search index. On content.published it projects the
/// item into a search_document (title + searchable text fields, per the plugin's
/// content-type definition); on content.unpublished it removes it. Cross-tenant
/// (no ambient tenant), so ids are explicit and query filters bypassed.
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
                            await HandleAsync(msg.Subject, e, stoppingToken);
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

    private async Task HandleAsync(string subject, IndexEvent e, CancellationToken ct)
    {
        using var scope = services.CreateScope();
        var cms = scope.ServiceProvider.GetRequiredService<CmsDbContext>();
        var search = scope.ServiceProvider.GetRequiredService<SearchDbContext>();

        if (subject == Subjects.ContentUnpublished)
        {
            var existing = await search.Documents.IgnoreQueryFilters()
                .FirstOrDefaultAsync(d => d.TenantId == e.TenantId && d.ContentItemId == e.ContentItemId, ct);
            if (existing is not null)
            {
                search.Documents.Remove(existing);
                await search.SaveChangesAsync(ct);
            }
            return;
        }

        // content.published — resolve the content type via the instance's plugin.
        var instance = await cms.PluginInstances.IgnoreQueryFilters().AsNoTracking()
            .FirstOrDefaultAsync(p => p.Id == e.PluginInstanceId, ct);
        if (instance is null)
        {
            return;
        }
        var typeDef = catalog.Find(instance.PluginId)?.ContentTypes.FirstOrDefault(t => t.Name == e.ContentType);
        if (typeDef is not { Searchable: true })
        {
            return; // not a searchable content type
        }

        var item = await cms.ContentItems.IgnoreQueryFilters().AsNoTracking()
            .FirstOrDefaultAsync(c => c.Id == e.ContentItemId, ct);
        if (item?.PublishedVersionId is not { } versionId)
        {
            return;
        }
        var version = await cms.ContentVersions.IgnoreQueryFilters().AsNoTracking()
            .FirstOrDefaultAsync(v => v.Id == versionId, ct);
        if (version is null)
        {
            return;
        }

        var (title, body) = Project(version.DataJson, typeDef);
        var url = $"/api/{instance.Slug}/{e.ContentType}/{e.Slug}";

        var doc = await search.Documents.IgnoreQueryFilters()
            .FirstOrDefaultAsync(d => d.TenantId == e.TenantId && d.ContentItemId == e.ContentItemId, ct);
        if (doc is null)
        {
            doc = new SearchDocument { Id = Guid.NewGuid(), TenantId = e.TenantId, ContentItemId = e.ContentItemId };
            search.Documents.Add(doc);
        }
        doc.PluginInstanceId = e.PluginInstanceId;
        doc.ContentType = e.ContentType;
        doc.Title = title;
        doc.Body = body;
        doc.Url = url;
        doc.UpdatedAt = DateTimeOffset.UtcNow;
        await search.SaveChangesAsync(ct);
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
