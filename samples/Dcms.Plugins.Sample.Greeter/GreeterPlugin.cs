using System.Text.Json;
using System.Text.Json.Nodes;
using Dcms.Plugins.Blog.Api;
using Dcms.Plugins.Forms.Api;
using Dcms.PluginSdk.Abstractions;
using Dcms.PluginSdk.Abstractions.Contracts;
using Dcms.PluginSdk.Abstractions.Data;
using Dcms.PluginSdk.Abstractions.Platform;
using Microsoft.AspNetCore.Http;

namespace Dcms.Plugins.Sample.Greeter;

// A contract of its own. A real plugin ships this in its own .Api assembly so that others can
// reference it without the implementation; the sample keeps one project for readability.

public sealed record HelloRequest(string? Name = null);

public sealed record Greeting(string Message, int PostsPublished, string? LatestPost);

/// <summary>Greets visitors with what the site's blog has been up to.</summary>
[DcmsContract("sample.greeter", 1, Description = "A friendly greeting that knows about the blog.")]
public interface IGreeter
{
    [Operation(OpRisk.Read, Expose = OpExposure.Site | OpExposure.Ai, Description = "Greets someone.")]
    Task<Greeting> HelloAsync(HelloRequest input, CancellationToken ct);
}

/// <summary>
/// The whole plugin: a manifest that declares what it offers and needs, and the classes it names.
/// The platform does the rest — routes are mounted per instance, the contract becomes a site
/// API call, an AI tool and a C# interface for other plugins, the handler and hook are wired up.
/// </summary>
public sealed class GreeterPlugin : IPlugin
{
    public const string Id = "sample-greeter";

    public PluginManifest Manifest { get; } = PluginManifest.Create(
        id: Id,
        name: "Greeter (sample)",
        description: "Sample external plugin: greets visitors, counts blog posts, turns away spam.",
        allowMultipleInstances: false,
        configJsonSchema: """
            {
              "type": "object",
              "properties": {
                "greeting": { "type": "string", "title": "Greeting", "default": "Hello" },
                "blockedWords": { "type": "array", "title": "Words that mark a form submission as spam", "items": { "type": "string" } }
              },
              "additionalProperties": false
            }
            """,
        provides: [ContractProvision.Of<IGreeter, Greeter>()],
        consumes:
        [
            ContractRequirement.Of<IPluginStorage>(),
            // Optional: the greeter works on a site without a blog or forms, it just knows less.
            ContractRequirement.Of<IBlogPosts>(optional: true),
            ContractRequirement.Of<IFormSubmissions>(optional: true),
        ],
        subscribes: [EventSubscription.Of<BlogPostPublished, CountPublishedPosts>()],
        intercepts: [HookSubscription.Of<FormSubmitting, SpamFilter>(priority: 100)],
        // A table on the plugin's admin page. (Everything in dcms.storage also shows up there on
        // its own, as "Stored data"; a data set of your own is for showing it the way you mean it.)
        dataSets: [DataSetDeclaration.Of<TurnedAway>("turned-away", "Turned away", "Form submissions the spam filter refused.")],
        category: "Samples",
        iconName: "Hand");

    // GET /api/{slug}/hello?name=Ada
    public void MapEndpoints(IPluginEndpointBuilder endpoints) =>
        endpoints.MapGet("/hello", async (string? name, IPluginContext context, CancellationToken ct) =>
            Results.Ok(await new Greeter(context).HelloAsync(new HelloRequest(name), ct)));

    internal static string? Setting(IPluginContext context, string key) =>
        context.Instance?.Config.RootElement is { ValueKind: JsonValueKind.Object } root
        && root.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
}

public sealed class Greeter(IPluginContext context) : IGreeter
{
    public async Task<Greeting> HelloAsync(HelloRequest input, CancellationToken ct)
    {
        var stats = await context.Contracts.Get<IPluginStorage>()
            .GetAsync(new DocumentAddress("stats", "posts", InstanceScoped: false), ct);
        var count = stats?.Data.GetProperty("published").GetInt32() ?? 0;

        // Another plugin's data, typed: no JSON parsing of someone else's content.
        string? latest = null;
        if (context.Contracts.TryGet<IBlogPosts>() is { } blog)
        {
            var page = await blog.ListAsync(new ContentPageRequest(PageSize: 1), ct);
            latest = page.Items.FirstOrDefault()?.Data.Title;
        }

        var greeting = GreeterPlugin.Setting(context, "greeting") ?? "Hello";
        return new Greeting($"{greeting}, {input.Name ?? "friend"}!", count, latest);
    }
}

/// <summary>An event handler: runs off the request, for every blog post published anywhere in the tenant.</summary>
public sealed class CountPublishedPosts : IPluginEventHandler<BlogPostPublished>
{
    public async Task HandleAsync(BlogPostPublished e, IPluginContext context, CancellationToken ct)
    {
        var storage = context.Contracts.Get<IPluginStorage>();
        var address = new DocumentAddress("stats", "posts", InstanceScoped: false);
        var current = await storage.GetAsync(address, ct);
        var count = (current?.Data.GetProperty("published").GetInt32() ?? 0) + 1;
        await storage.PutAsync(new PutDocument("stats", "posts",
            JsonSerializer.SerializeToElement(new { published = count, lastSlug = e.Slug }),
            ExpectedVersion: current?.Version ?? 0, InstanceScoped: false), ct);
    }
}

/// <summary>A hook: runs before a form submission is stored, and may refuse it.</summary>
public sealed class SpamFilter : IPluginHookHandler<FormSubmitting>
{
    public async ValueTask<HookResult<FormSubmitting>> HandleAsync(FormSubmitting hook, IPluginContext context, CancellationToken ct)
    {
        var blocked = context.Instance?.Config.RootElement is { ValueKind: JsonValueKind.Object } root
                      && root.TryGetProperty("blockedWords", out var list) && list.ValueKind == JsonValueKind.Array
            ? list.EnumerateArray().Select(w => w.GetString()).OfType<string>().ToList()
            : [];
        var spam = hook.Data.Values
            .Where(v => v.ValueKind == JsonValueKind.String)
            .Any(v => blocked.Any(w => v.GetString()!.Contains(w, StringComparison.OrdinalIgnoreCase)));

        if (!spam)
        {
            return HookResult.Continue(hook);
        }
        // Kept so an admin can check the filter is not turning away real people. Best effort: a
        // hook that throws is skipped (hooks fail open), and the refusal must not hinge on a log.
        try
        {
            await context.Contracts.Get<IPluginStorage>().PutAsync(new PutDocument(TurnedAway.Collection, Guid.NewGuid().ToString("N"),
                JsonSerializer.SerializeToElement(new { form = hook.FormName, data = hook.Data, at = DateTimeOffset.UtcNow }),
                InstanceScoped: false), ct);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
        }
        return HookResult.Cancel(hook, "This message looks like spam.");
    }
}

/// <summary>
/// A data set: what the admin sees on the plugin's page. Describe the columns, answer a page of
/// rows, and say what may be done — here, deleting a row. The console does the rest.
/// </summary>
public sealed class TurnedAway(IPluginContext context) : IPluginDataSet
{
    public const string Collection = "turned-away";

    private IPluginStorage Storage => context.Contracts.Get<IPluginStorage>();

    public Task<DataSetSchema> DescribeAsync(CancellationToken ct) => Task.FromResult(new DataSetSchema(
        Columns:
        [
            new DataColumn("form", "Form", DataColumnKinds.Badge),
            new DataColumn("data", "What was sent", DataColumnKinds.Json, Primary: true),
            new DataColumn("at", "When", DataColumnKinds.DateTime),
        ],
        CanDelete: true));

    public async Task<DataPage> ListAsync(DataQuery query, CancellationToken ct)
    {
        var page = await Storage.QueryAsync(
            new QueryDocuments(Collection, Page: query.Page, PageSize: query.PageSize, InstanceScoped: false), ct);
        return new DataPage(page.Items.Select(ToRow).ToList(), page.TotalCount);
    }

    public async Task<DataRow?> GetAsync(string key, CancellationToken ct) =>
        await Storage.GetAsync(new DocumentAddress(Collection, key, InstanceScoped: false), ct) is { } doc ? ToRow(doc) : null;

    public async Task<bool> DeleteAsync(string key, CancellationToken ct) =>
        (await Storage.DeleteAsync(new DocumentAddress(Collection, key, InstanceScoped: false), ct)).Found;

    private static DataRow ToRow(StoredDocument doc) =>
        new(doc.Key, JsonNode.Parse(doc.Data.GetRawText())!.AsObject());
}
