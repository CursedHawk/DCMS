using System.Text;
using System.Text.Json.Nodes;

namespace Dcms.AdminApi.ApiClientGen;

/// <summary>
/// Packs a downloadable Vite + React starter site for a tenant: the committed
/// <c>@dcms/site-template-react</c> scaffold (embedded at build time) with its
/// <c>src/api/</c> replaced by the tenant's generated, self-contained typed client and
/// a <c>.env</c> pre-filled from the tenant's domain and a demo instance. The result
/// builds with only npm/pnpm + the pinned React/Vite toolchain — no workspace needed.
/// </summary>
public static class SiteStarterEmitter
{
    private const string TemplatePrefix = "Dcms.AdminApi.SiteTemplate/";

    public static byte[] Build(JsonObject openApi, IReadOnlyList<GeneratedInstance> instances, string? apiBaseUrl)
    {
        var files = new Dictionary<string, string>(StringComparer.Ordinal);

        // 1. Base template files (embedded workspace scaffold).
        foreach (var name in PackageIo.EmbeddedNames(TemplatePrefix))
        {
            var relative = name[TemplatePrefix.Length..].Replace('\\', '/');
            files[relative] = PackageIo.ReadEmbedded(name);
        }

        // 2. Replace src/api/ with the tenant's generated, self-contained typed client.
        foreach (var (path, content) in TypeScriptClientEmitter.Emit(openApi, instances, "./runtime"))
        {
            files["src/api/" + path["src/".Length..]] = content; // src/index.ts -> src/api/index.ts
        }
        files["src/api/runtime/index.ts"] = ClientPackageBuilder.RuntimeIndex;
        files["src/api/runtime/types.ts"] = ClientPackageBuilder.RuntimeTypes;

        // 3. Self-contained package.json (drop the workspace dep — the runtime is vendored).
        files["package.json"] = SelfContainedPackageJson;

        // Approve esbuild's install script (pnpm 10+ gates build scripts; this is the
        // settings home in pnpm 11). Harmless for npm/yarn, which run it by default.
        files["pnpm-workspace.yaml"] = "allowBuilds:\n  esbuild: true\n";

        // 4. Pre-fill .env for this tenant.
        files[".env"] = BuildEnv(openApi, apiBaseUrl);

        return PackageIo.Zip(files);
    }

    private static string BuildEnv(JsonObject openApi, string? apiBaseUrl)
    {
        var (slug, contentType) = FirstContentType(openApi);
        return new StringBuilder()
            .Append("VITE_API_BASE_URL=").Append(apiBaseUrl ?? string.Empty).Append('\n')
            .Append("VITE_DEMO_SLUG=").Append(slug ?? string.Empty).Append('\n')
            .Append("VITE_DEMO_CONTENT_TYPE=").Append(contentType ?? string.Empty).Append('\n')
            .ToString();
    }

    // First content list endpoint: /api/{slug}/{contentType} with a GET.
    private static (string? Slug, string? ContentType) FirstContentType(JsonObject openApi)
    {
        if (openApi["paths"] is not JsonObject paths)
        {
            return (null, null);
        }
        foreach (var (key, node) in paths.OrderBy(p => p.Key, StringComparer.Ordinal))
        {
            if (!key.StartsWith("/api/", StringComparison.Ordinal))
            {
                continue;
            }
            var parts = key["/api/".Length..].Split('/');
            if (parts.Length != 2 || parts[1].Contains('{'))
            {
                continue;
            }
            if ((node as JsonObject)?["get"] is not null)
            {
                return (parts[0], parts[1]);
            }
        }
        return (null, null);
    }

    private const string SelfContainedPackageJson = """
        {
          "name": "dcms-site",
          "private": true,
          "version": "0.1.0",
          "type": "module",
          "scripts": {
            "dev": "vite",
            "build": "tsc --noEmit && vite build",
            "preview": "vite preview"
          },
          "dependencies": {
            "react": "^19",
            "react-dom": "^19"
          },
          "devDependencies": {
            "@types/react": "^19",
            "@types/react-dom": "^19",
            "@vitejs/plugin-react": "^5",
            "typescript": "^5",
            "vite": "^7"
          }
        }
        """;
}
