using System.Text.Json.Nodes;

namespace Dcms.AdminApi.ApiClientGen;

/// <summary>
/// Packs a self-contained, downloadable TypeScript client zip: the emitted per-tenant
/// layer (<c>src/types.ts</c> + <c>src/index.ts</c>) plus the vendored
/// <c>@dcms/api-client</c> runtime (embedded from the workspace package at build time)
/// under <c>src/runtime/</c>, so the download compiles with nothing but TypeScript.
/// </summary>
public static class ClientPackageBuilder
{
    // The @dcms/api-client runtime source, embedded from the workspace package.
    internal static string RuntimeIndex => PackageIo.ReadEmbedded("Dcms.AdminApi.ApiClientGen.Runtime.index.ts");

    internal static string RuntimeTypes => PackageIo.ReadEmbedded("Dcms.AdminApi.ApiClientGen.Runtime.types.ts");

    public static byte[] Build(JsonObject openApi, IReadOnlyList<GeneratedInstance> instances)
    {
        var files = new Dictionary<string, string>(StringComparer.Ordinal);

        // Generated layer imports the runtime by relative path (self-contained).
        foreach (var (path, content) in TypeScriptClientEmitter.Emit(openApi, instances, "./runtime"))
        {
            files[path] = content;
        }

        files["src/runtime/index.ts"] = RuntimeIndex;
        files["src/runtime/types.ts"] = RuntimeTypes;
        files["package.json"] = PackageJson;
        files["tsconfig.json"] = TsConfig;
        files["README.md"] = Readme;
        files[".gitignore"] = "node_modules/\ndist/\n";

        return PackageIo.Zip(files);
    }

    private const string PackageJson = """
        {
          "name": "@dcms-site/api-client",
          "private": true,
          "version": "1.0.0",
          "type": "module",
          "main": "src/index.ts",
          "types": "src/index.ts",
          "scripts": {
            "typecheck": "tsc --noEmit"
          },
          "devDependencies": {
            "typescript": "^5"
          }
        }
        """;

    private const string TsConfig = """
        {
          "compilerOptions": {
            "target": "ES2022",
            "lib": ["ES2022", "DOM"],
            "module": "ESNext",
            "moduleResolution": "bundler",
            "strict": true,
            "declaration": true,
            "outDir": "dist",
            "skipLibCheck": true,
            "isolatedModules": true,
            "verbatimModuleSyntax": true
          },
          "include": ["src"]
        }
        """;

    private const string Readme = """
        # Tenant API client

        Auto-generated, fully-typed TypeScript client for this tenant's content API.
        It is self-contained — the runtime is vendored under `src/runtime/`, so it
        compiles with nothing but TypeScript. Re-download after changing plugins to
        regenerate the types.

        ```ts
        import { createTenantClient } from './src';

        // Same-origin on the published site; or pass the tenant's API base URL.
        const api = createTenantClient({ baseUrl: '' });

        // Each configured plugin instance is exposed by its slug. For a blog
        // instance with slug "news":
        const { items } = await api.news.post.list({ page: 1 });
        const post = await api.news.post.get('my-first-post');

        // Or generically, without knowing the slugs at author time:
        const page = await api.content('news', 'post').list();

        // Media fields are asset ids; resolve them to URLs:
        const src = api.media.url(post.data.coverImage!, 'webp-640');
        ```
        """;
}
