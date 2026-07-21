extern alias AdminApiApp;

using System.IO.Compression;
using System.Text.Json.Nodes;
using AdminApiApp::Dcms.AdminApi.ApiClientGen;

namespace Dcms.IntegrationTests.ApiClientGen;

/// <summary>
/// Unit coverage for the TypeScript client emitter. Uses a hand-built OpenAPI
/// fixture (mirroring what OpenApiAssembler emits) so it needs no host/Testcontainers
/// and runs stand-alone: dotnet test --filter FullyQualifiedName~TypeScriptClientEmitter
/// </summary>
public class TypeScriptClientEmitterTests
{
    private static JsonObject SampleSpec() => new()
    {
        ["openapi"] = "3.1.0",
        ["paths"] = new JsonObject
        {
            ["/api/devblog/post"] = new JsonObject { ["get"] = new JsonObject() },
            ["/api/devblog/post/{slug}"] = new JsonObject { ["get"] = new JsonObject() },
            ["/api/stats/collect"] = new JsonObject { ["post"] = new JsonObject() },
        },
        ["components"] = new JsonObject
        {
            ["schemas"] = new JsonObject
            {
                ["devblog_post"] = new JsonObject
                {
                    ["type"] = "object",
                    ["properties"] = new JsonObject
                    {
                        ["id"] = new JsonObject { ["type"] = "string", ["format"] = "uuid" },
                        ["slug"] = new JsonObject { ["type"] = "string" },
                        ["data"] = new JsonObject
                        {
                            ["type"] = "object",
                            ["properties"] = new JsonObject
                            {
                                ["title"] = new JsonObject { ["type"] = "string" },
                                ["excerpt"] = new JsonObject { ["type"] = "string" },
                                ["body"] = new JsonObject { ["type"] = "string", ["x-dcms-format"] = "richtext" },
                                ["coverImage"] = new JsonObject { ["type"] = "string", ["format"] = "uuid", ["x-dcms-media-category"] = "image" },
                                ["tags"] = new JsonObject { ["type"] = "array", ["items"] = new JsonObject { ["type"] = "string" } },
                            },
                            ["required"] = new JsonArray { "title", "body" },
                        },
                    },
                },
                ["devblog_post_list"] = new JsonObject { ["type"] = "object" },
                // Analytics request-body schema — must NOT become a content interface.
                ["stats_event"] = new JsonObject
                {
                    ["type"] = "object",
                    ["properties"] = new JsonObject { ["type"] = new JsonObject { ["type"] = "string" } },
                },
            },
        },
    };

    private static IReadOnlyList<GeneratedInstance> SampleInstances() =>
    [
        new("devblog", "blog", "Dev Blog"),
        new("find", "search", "Search"),
        new("members", "visitor-auth", "Members"),
        new("support", "live-chat", "Support"),
        new("stats", "analytics", "Stats"),
    ];

    [Fact]
    public void Emits_typed_interfaces_from_content_field_schemas()
    {
        var files = TypeScriptClientEmitter.Emit(SampleSpec(), SampleInstances());
        WriteIfRequested(files);

        var types = files["src/types.ts"];
        Assert.Contains("export interface DevblogPost {", types);
        Assert.Contains("title: string;", types);
        Assert.Contains("body: string;", types);        // richtext maps to string
        Assert.Contains("coverImage?: MediaRef;", types); // media ref, optional
        Assert.Contains("tags?: string[];", types);
        Assert.Contains("excerpt?: string;", types);     // not required -> optional
        Assert.Contains("import type { MediaRef } from '@dcms/api-client';", types);

        // The paged envelope and the analytics event schema are not content interfaces.
        Assert.DoesNotContain("DevblogPostList", types);
        Assert.DoesNotContain("StatsEvent", types);
    }

    [Fact]
    public void Emits_typed_content_resolvers_grouped_by_instance_slug()
    {
        var files = TypeScriptClientEmitter.Emit(SampleSpec(), SampleInstances());
        var index = files["src/index.ts"];

        Assert.Contains("export function createTenantClient(options: TenantClientOptions = {}) {", index);
        Assert.Contains("\"devblog\": {", index);
        Assert.Contains("\"post\": {", index);
        Assert.Contains("list: (params?: ListParams): Promise<PagedResult<DevblogPost>> => http.listContent<DevblogPost>(\"devblog\", \"post\", params)", index);
        Assert.Contains("get: (slug: string): Promise<ContentItem<DevblogPost>> => http.getContent<DevblogPost>(\"devblog\", \"post\", slug)", index);
        Assert.Contains("import type { DevblogPost } from './types';", index);
        Assert.Contains("export type TenantClient = ReturnType<typeof createTenantClient>;", index);
    }

    [Fact]
    public void Emits_platform_capability_helpers_keyed_by_plugin_id()
    {
        var files = TypeScriptClientEmitter.Emit(SampleSpec(), SampleInstances());
        var index = files["src/index.ts"];

        Assert.Contains("search: (params: SearchParams): Promise<SearchResult> => http.search(\"find\", params)", index);
        Assert.Contains("auth: http.visitorAuth(\"members\")", index);
        Assert.Contains("history: (conversationId: string): Promise<ChatMessage[]> => http.chatHistory(\"support\", conversationId)", index);
        Assert.Contains("collect: (event: AnalyticsEvent): Promise<void> => http.collect(event, \"stats\")", index);
        Assert.Contains("url: (assetId: string, variant?: string) => http.mediaUrl(assetId, variant)", index);

        // Type-only imports for the capabilities that were emitted.
        Assert.Contains("type SearchParams", index);
        Assert.Contains("type ChatMessage", index);
        Assert.Contains("type AnalyticsEvent", index);
    }

    [Fact]
    public void Packs_a_self_contained_client_zip()
    {
        var zip = ClientPackageBuilder.Build(SampleSpec(), SampleInstances());

        using var archive = new ZipArchive(new MemoryStream(zip), ZipArchiveMode.Read);
        var entries = archive.Entries.Select(e => e.FullName.Replace('\\', '/')).ToHashSet();

        Assert.Contains("src/index.ts", entries);
        Assert.Contains("src/types.ts", entries);
        Assert.Contains("src/runtime/index.ts", entries);   // vendored runtime
        Assert.Contains("src/runtime/types.ts", entries);
        Assert.Contains("package.json", entries);
        Assert.Contains("tsconfig.json", entries);
        Assert.Contains("README.md", entries);

        // Optional extraction for manual `tsc` verification of the whole package.
        var outDir = Environment.GetEnvironmentVariable("DCMS_PKG_OUT");
        if (!string.IsNullOrEmpty(outDir))
        {
            Directory.CreateDirectory(outDir);
            new ZipArchive(new MemoryStream(zip), ZipArchiveMode.Read).ExtractToDirectory(outDir, overwriteFiles: true);
        }
    }

    [Fact]
    public void Packs_a_runnable_site_starter_zip()
    {
        var zip = SiteStarterEmitter.Build(SampleSpec(), SampleInstances(), "https://acme.example");

        using var archive = new ZipArchive(new MemoryStream(zip), ZipArchiveMode.Read);
        var entries = archive.Entries.Select(e => e.FullName.Replace('\\', '/')).ToHashSet();

        // Scaffold + overlaid generated client + vendored runtime + tenant .env.
        Assert.Contains("package.json", entries);
        Assert.Contains("vite.config.ts", entries);
        Assert.Contains("index.html", entries);
        Assert.Contains("src/main.tsx", entries);
        Assert.Contains("src/App.tsx", entries);
        Assert.Contains("src/api/index.ts", entries);
        Assert.Contains("src/api/runtime/index.ts", entries);
        Assert.Contains(".env", entries);

        // src/api is the generated client, not the placeholder.
        Assert.Contains("createTenantClient", ReadEntry(archive, "src/api/index.ts"));

        // package.json is self-contained: the workspace dependency is dropped.
        Assert.DoesNotContain("@dcms/api-client", ReadEntry(archive, "package.json"));

        // .env is pre-filled from the tenant's first content instance and domain.
        var env = ReadEntry(archive, ".env");
        Assert.Contains("VITE_API_BASE_URL=https://acme.example", env);
        Assert.Contains("VITE_DEMO_SLUG=devblog", env);
        Assert.Contains("VITE_DEMO_CONTENT_TYPE=post", env);

        var outDir = Environment.GetEnvironmentVariable("DCMS_STARTER_OUT");
        if (!string.IsNullOrEmpty(outDir))
        {
            Directory.CreateDirectory(outDir);
            new ZipArchive(new MemoryStream(zip), ZipArchiveMode.Read).ExtractToDirectory(outDir, overwriteFiles: true);
        }
    }

    private static string ReadEntry(ZipArchive archive, string path)
    {
        using var stream = archive.GetEntry(path)!.Open();
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    // Optional side effect for manual TS-compile verification: set DCMS_EMIT_OUT to a
    // directory and the emitted files are written there so tsc can check them.
    private static void WriteIfRequested(IReadOnlyDictionary<string, string> files)
    {
        var outDir = Environment.GetEnvironmentVariable("DCMS_EMIT_OUT");
        if (string.IsNullOrEmpty(outDir))
        {
            return;
        }
        foreach (var (path, content) in files)
        {
            var full = Path.Combine(outDir, path);
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            File.WriteAllText(full, content);
        }
    }
}
