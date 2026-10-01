using System.Text.Json;
using System.Text.Json.Nodes;
using Dcms.Plugins.Blog.Api;
using Dcms.Plugins.Forms.Api;
using Dcms.Plugins.Sample.Guestbook.Api;
using Dcms.Plugins.VisitorAuth.Api;
using Dcms.PluginSdk.Abstractions;
using Dcms.PluginSdk.Abstractions.Contracts;
using Dcms.PluginSdk.Abstractions.Data;
using Dcms.PluginSdk.Abstractions.Platform;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Dcms.Plugins.Sample.Guestbook;

/// <summary>
/// The Guestbook sample: visitors sign a guestbook on the tenant's site, moderators approve
/// what shows. Small on purpose, and built to use every part of the plugin system once, each
/// with a comment saying what it is for. Read it beside docs/plugins.md.
///
/// <list type="bullet">
/// <item>Manifest: permissions with descriptions and a default grant, config schema with a
/// custom widget, public config keys, a content type, data sets, admin screens with menu entries.</item>
/// <item>Contracts: provides <c>guestbook.entries@1</c> (its .Api project) with a hook and two
/// events; consumes platform services and, optionally, Blog, Forms and VisitorAuth.</item>
/// <item>Reactions: intercepts its own hook and the Forms plugin's; handles its own event and the
/// Blog's; an interval job and an on-demand one.</item>
/// <item>Routes: site, admin and host, with permissions, public declarations and audit names
/// stated through the SDK; an OpenAPI fragment so sites get a typed client.</item>
/// <item>Admin UI: admin/ (React, @dcms/plugin-ui), built with the SDK's Vite preset and loaded
/// by the console from the plugin's folder.</item>
/// </list>
/// </summary>
public sealed class GuestbookPlugin : IPlugin
{
    public const string Id = "sample-guestbook";

    /// <summary>The tenant's settings per guestbook, edited on the plugin page; validated server-side.</summary>
    private const string ConfigSchema = """
        {
          "type": "object",
          "properties": {
            "title": { "type": "string", "title": "Heading on the site", "default": "Sign our guestbook" },
            "moderation": {
              "type": "string", "title": "Moderation", "enum": ["pre", "post"], "default": "pre",
              "description": "pre: entries wait for approval. post: they show at once and can be hidden later."
            },
            "blockedWords": {
              "type": "array", "title": "Words to mask", "items": { "type": "string" },
              "description": "Replaced with asterisks in signatures, on top of the platform-wide list."
            },
            "accent": {
              "type": "string", "title": "Accent colour", "format": "guestbook-accent", "default": "amber",
              "enum": ["amber", "rose", "teal", "indigo", "slate"]
            }
          },
          "additionalProperties": false
        }
        """;

    public PluginManifest Manifest { get; } = PluginManifest.Create(
        id: Id,
        name: "Guestbook (sample)",
        description: "A guestbook visitors sign on your site, with moderation, exports and a daily digest. "
                     + "The reference plugin for developers: it uses every part of the plugin system.",
        allowMultipleInstances: true,
        configJsonSchema: ConfigSchema,
        // Keys of the config a published site may read (GET /api/{slug}/_config). Everything else
        // stays private to the tenant.
        publicConfigKeys: ["title", "accent"],
        // plugin:sample-guestbook:{action}. Descriptions show in the role editor; `read` is
        // granted to the Member role when a tenant adds its first guestbook. Owners hold all.
        permissions:
        [
            new PermissionDefinition("read", "View guestbooks", "See entries and counts of every guestbook.", GrantToMembers: true),
            new PermissionDefinition("moderate", "Moderate guestbooks", "Approve, reject, edit, reply to and delete entries."),
            new PermissionDefinition("export", "Export guestbooks", "Download a guestbook's entries as a spreadsheet."),
        ],
        // A content type: authored in Content like a blog post, delivered by the standard routes
        // (GET /api/{slug}/notice), and raising no events of its own.
        contentTypes:
        [
            new ContentTypeDefinition(
                Name: "notice",
                Fields:
                [
                    new ContentFieldDefinition("title", ContentFieldType.Text, Required: true, "Notice title"),
                    new ContentFieldDefinition("body", ContentFieldType.RichText, Required: false, "Shown above the guestbook"),
                ],
                Searchable: false,
                SlugField: "title"),
        ],
        provides: [ContractProvision.Of<IGuestbook, Guestbook>()],
        consumes:
        [
            ContractRequirement.Of<IPluginStorage>(),
            ContractRequirement.Of<IPluginBlobs>(),
            ContractRequirement.Of<IPluginCache>(),
            ContractRequirement.Of<IPluginEvents>(),
            ContractRequirement.Of<IPluginJobs>(),
            ContractRequirement.Of<IPluginNotifications>(),
            ContractRequirement.Of<IPluginEmail>(),
            // Optional: the guestbook works without them and does more with them.
            ContractRequirement.Of<IBlogPosts>(optional: true),
            ContractRequirement.Of<IFormSubmissions>(optional: true),
            ContractRequirement.Of<IVisitorIdentity>(optional: true),
        ],
        subscribes:
        [
            EventSubscription.Of<EntrySigned, NotifyModerators>(),
            EventSubscription.Of<BlogPostPublished, CountBlogPosts>(),
        ],
        intercepts:
        [
            HookSubscription.Of<EntrySigning, MaskBlockedWords>(priority: 100),
            HookSubscription.Of<FormSubmitting, RefuseSpamForms>(priority: 100),
        ],
        jobs:
        [
            JobDeclaration.Of<DailyDigest>("daily-digest", TimeSpan.FromDays(1)),
            JobDeclaration.Of<ExportEntries>("export"),
        ],
        // Tables on the instance page. dcms.storage and dcms.blobs add "Stored data" and "Files".
        dataSets:
        [
            DataSetDeclaration.Of<EntriesDataSet>("entries", "Entries", "Every signature, with its moderation status.",
                readPermission: "read", writePermission: "moderate", iconName: "NotebookPen"),
        ],
        // Screens in the admin console, drawn by admin/src (the Vite-built UI module).
        adminScreens:
        [
            new AdminScreen("overview", "Guestbooks", AdminScreenScope.Plugin, Permission: "read", IconName: "BookOpen",
                Nav: new AdminNavPlacement("main", 60), Titles: new Dictionary<string, string> { ["cs"] = "Knihy návštěv" },
                Description: "Every guestbook with what waits for approval."),
            new AdminScreen("moderation", "Moderation", AdminScreenScope.Instance, Permission: "moderate", IconName: "ShieldCheck",
                Titles: new Dictionary<string, string> { ["cs"] = "Moderace" },
                Description: "Approve or reject what visitors wrote."),
        ],
        category: "Samples",
        summary: "A moderated guestbook; the reference plugin for developers.",
        iconName: "BookOpen",
        tags: ["sample", "engagement"]);

    /// <summary>Per host: the operator's settings (Plugins:sample-guestbook:*) and what this plugin needs from DI.</summary>
    public void ConfigureServices(IServiceCollection services, PluginHost host)
    {
        var options = new GuestbookOptions();
        host.SettingsFor(Id).Bind(options);
        services.AddSingleton(options);
        services.AddHttpContextAccessor();
    }

    /// <summary>Site plane: <c>/api/{slug}/...</c> for each enabled guestbook.</summary>
    public void MapEndpoints(IPluginEndpointBuilder endpoints)
    {
        // The content type's standard routes, which the default implementation would have mapped.
        endpoints.MapContentList("notice");
        endpoints.MapContentGetBySlug("notice");

        endpoints.MapGet("/entries", (int? page, IPluginContext context, GuestbookOptions options, IHttpContextAccessor http,
                CancellationToken ct) => new Guestbook(context, options, http).ListAsync(new ListEntries(page ?? 1), ct))
            .WithoutPermission("Approved entries are what the guestbook shows to everyone.");

        endpoints.MapPost("/sign", async (SignGuestbook body, IPluginContext context, GuestbookOptions options,
                IHttpContextAccessor http, CancellationToken ct) =>
            {
                try
                {
                    return Results.Ok(await new Guestbook(context, options, http).SignAsync(body, ct));
                }
                catch (ContractValidationException e)
                {
                    return Results.BadRequest(new { error = e.Message });
                }
                catch (ContractLimitException e)
                {
                    return Results.Json(new { error = e.Message }, statusCode: StatusCodes.Status429TooManyRequests);
                }
            })
            .WithoutPermission("Any visitor may sign; signatures are throttled per address and moderated.")
            .AuditAs("entry.signed");
    }

    /// <summary>Admin plane: <c>/api/admin/plugins/{slug}/...</c>, for signed-in members.</summary>
    public void MapAdminEndpoints(IPluginEndpointBuilder endpoints)
    {
        endpoints.MapGet("/stats", (IPluginContext context, GuestbookOptions options, IHttpContextAccessor http, CancellationToken ct) =>
                new Guestbook(context, options, http).StatsAsync(ct))
            .RequirePluginPermission("read");

        // The public look of the guestbook (title, accent), for the admin screens to match it.
        endpoints.MapGet("/appearance", (IPluginContext context) => Results.Ok(new
            {
                title = Guestbook.Setting(context.Instance!, "title"),
                accent = Guestbook.Setting(context.Instance!, "accent"),
            }))
            .RequirePluginPermission("read");

        endpoints.MapPost("/entries/{entryId}/approve", async (string entryId, IPluginContext context, GuestbookOptions options,
                IHttpContextAccessor http, CancellationToken ct) =>
            {
                try
                {
                    return Results.Ok(await new Guestbook(context, options, http).ApproveAsync(new EntryRef(entryId), ct));
                }
                catch (ContractValidationException e)
                {
                    return Results.NotFound(new { error = e.Message });
                }
            })
            .RequirePluginPermission("moderate")
            .AuditAs("entry.approved");

        // dcms.jobs: the export runs in the background; the request returns at once.
        endpoints.MapPost("/export", async (ExportRequest body, IPluginContext context, CancellationToken ct) =>
            {
                await context.Contracts.Get<IPluginJobs>().EnqueueAsync(new JobEnqueue("export",
                    JsonSerializer.SerializeToElement(new { email = body.Email }),
                    DedupeKey: $"export:{context.Instance!.InstanceId:N}"), ct);
                return Results.Accepted();
            })
            .RequirePluginPermission("export")
            .AuditAs("export.requested");
    }

    /// <summary>
    /// Host routes, outside any instance: a tenant-wide summary for the overview screen. The
    /// runtime gives the handler the plugin's tenant-wide context (no instance).
    /// </summary>
    public void MapHostEndpoints(IEndpointRouteBuilder app, PluginHost host)
    {
        if (!host.IsAdmin)
        {
            return;
        }
        app.MapGet("/api/admin/guestbook/overview", async (IPluginContext context, CancellationToken ct) =>
            {
                var store = new EntryStore(context);
                var rows = new List<object>();
                foreach (var guestbook in await store.KnownAsync(ct))
                {
                    rows.Add(new
                    {
                        guestbook.InstanceId,
                        guestbook.Slug,
                        guestbook.Name,
                        pending = await store.CountAsync(guestbook.InstanceId, EntryStatus.Pending, ct),
                        approved = await store.CountAsync(guestbook.InstanceId, EntryStatus.Approved, ct),
                    });
                }
                var blog = await context.Contracts.Get<IPluginStorage>()
                    .GetAsync(new DocumentAddress("stats", "blog", InstanceScoped: false), ct);
                return Results.Ok(new
                {
                    guestbooks = rows,
                    blogPostsSeen = blog?.Data.GetProperty("published").GetInt32() ?? 0,
                });
            })
            .RequirePluginPermission("read");
    }

    /// <summary>The site routes in the tenant's OpenAPI document, so a site's generated client can call them.</summary>
    public OpenApiFragment BuildOpenApiFragment(PluginInstanceContext instance)
    {
        var entry = new JsonObject
        {
            ["type"] = "object",
            ["properties"] = new JsonObject
            {
                ["id"] = new JsonObject { ["type"] = "string" },
                ["name"] = new JsonObject { ["type"] = "string" },
                ["message"] = new JsonObject { ["type"] = "string" },
                ["signedAt"] = new JsonObject { ["type"] = "string", ["format"] = "date-time" },
                ["reply"] = new JsonObject { ["type"] = new JsonArray("string", "null") },
            },
        };
        var own = new OpenApiFragment(instance.Name, instance.Description,
        [
            new OpenApiPathFragment("/entries", "get", $"{instance.Slug}_entries", "List guestbook entries",
                $"{instance.Description}\n\nApproved entries, newest first.",
                new JsonObject
                {
                    ["type"] = "object",
                    ["properties"] = new JsonObject
                    {
                        ["entries"] = new JsonObject { ["type"] = "array", ["items"] = entry },
                        ["total"] = new JsonObject { ["type"] = "integer" },
                    },
                },
                ClientPath: ["entries", "list"]),
            new OpenApiPathFragment("/sign", "post", $"{instance.Slug}_sign", "Sign the guestbook",
                $"{instance.Description}\n\nStores a signature; pre-moderated guestbooks hold it for approval.",
                new JsonObject { ["type"] = "object" },
                RequestBodySchema: new JsonObject
                {
                    ["type"] = "object",
                    ["required"] = new JsonArray("name", "message"),
                    ["properties"] = new JsonObject
                    {
                        ["name"] = new JsonObject { ["type"] = "string", ["maxLength"] = 80 },
                        ["message"] = new JsonObject { ["type"] = "string" },
                    },
                },
                ClientPath: ["entries", "sign"]),
        ], new Dictionary<string, JsonNode>());
        return OpenApiFragment.Merge([own, ContentApiFragment.ForContentTypes(instance, Manifest)]);
    }
}

/// <summary>Body of <c>POST /export</c>: where to send word that the file is ready, if anywhere.</summary>
public sealed record ExportRequest(string? Email = null);
