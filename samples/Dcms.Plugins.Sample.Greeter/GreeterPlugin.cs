using System.Text.Json;
using Dcms.Plugins.Blog.Api;
using Dcms.Plugins.Forms.Api;
using Dcms.PluginSdk.Abstractions;
using Dcms.PluginSdk.Abstractions.Contracts;
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
    public ValueTask<HookResult<FormSubmitting>> HandleAsync(FormSubmitting hook, IPluginContext context, CancellationToken ct)
    {
        var blocked = context.Instance?.Config.RootElement is { ValueKind: JsonValueKind.Object } root
                      && root.TryGetProperty("blockedWords", out var list) && list.ValueKind == JsonValueKind.Array
            ? list.EnumerateArray().Select(w => w.GetString()).OfType<string>().ToList()
            : [];
        var spam = hook.Data.Values
            .Where(v => v.ValueKind == JsonValueKind.String)
            .Any(v => blocked.Any(w => v.GetString()!.Contains(w, StringComparison.OrdinalIgnoreCase)));

        return ValueTask.FromResult(spam
            ? HookResult.Cancel(hook, "This message looks like spam.")
            : HookResult.Continue(hook));
    }
}
