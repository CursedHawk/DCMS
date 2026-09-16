using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Dcms.AdminApi.ApiClientGen;

/// <summary>A tenant plugin instance the generated client should resolve.</summary>
public sealed record GeneratedInstance(string Slug, string PluginId, string Name, string Description = "");

/// <summary>
/// Emits the per-tenant TypeScript layer of a site's API client — <c>types.ts</c>,
/// <c>index.ts</c> and <c>API.md</c> — from the tenant's assembled OpenAPI document and its
/// enabled plugin instances, layered on the hand-written <c>@dcms/api-client</c> runtime
/// imported from <paramref name="runtimeModule"/>.
///
/// <para><b>Driven by the document, not by a list of plugins.</b> Every operation a plugin
/// contributes becomes a typed method, named by the plugin itself through <c>x-dcms-client</c>
/// (<see cref="Dcms.PluginSdk.Abstractions.OpenApiPathFragment.ClientPath"/>) or, for the
/// list + get-by-slug shape every content plugin shares, derived. The generator used to recognise
/// content endpoints by URL shape alone, which silently dropped the Forms plugin's submissions and
/// turned Branding's single object into a paged <c>list()</c> that 404s — a plugin was only in
/// the client if the generator already knew about it.</para>
///
/// <para>Four plugins still get hand-written helpers — search, visitor-auth, live-chat and
/// analytics — because their endpoints are not in the document at all (their fragments are
/// empty) or have a richer runtime type than the document can express. Those are keyed by plugin
/// id in <see cref="CapabilityPlugins"/>, and are the only place a plugin id appears here.</para>
///
/// <para>Also emitted: <c>collections</c> and <c>forms</c>, metadata a page can be written against
/// without knowing the tenant's slugs — which is what lets one starter template render real content
/// for any tenant — and <c>API.md</c>, a compact reference an author or the IDE agent reads in a
/// few hundred tokens instead of an OpenAPI document of tens of thousands.</para>
/// </summary>
public static class TypeScriptClientEmitter
{
    public const string DefaultRuntimeModule = "@dcms/api-client";

    /// <summary>Plugins served by a runtime helper rather than by their OpenAPI operations.</summary>
    internal static readonly IReadOnlySet<string> CapabilityPlugins =
        new HashSet<string>(StringComparer.Ordinal) { "search", "visitor-auth", "live-chat", "analytics" };

    /// <summary>Top-level client members an instance slug must not shadow.</summary>
    private static readonly IReadOnlySet<string> ReservedTopLevel =
        new HashSet<string>(StringComparer.Ordinal) { "media", "content", "submitForm", "call", "tags" };

    private static readonly string[] Methods = ["get", "post", "put", "patch", "delete"];

    public static IReadOnlyDictionary<string, string> Emit(
        JsonObject openApi,
        IReadOnlyList<GeneratedInstance> instances,
        string runtimeModule = DefaultRuntimeModule)
    {
        var model = new Model(openApi, instances);
        model.Build();

        return new Dictionary<string, string>
        {
            ["src/types.ts"] = model.RenderTypes(runtimeModule),
            ["src/index.ts"] = model.RenderIndex(runtimeModule),
            ["src/API.md"] = model.RenderMarkdown(),
        };
    }

    /// <summary>One callable member of the client, and what the reference says about it.</summary>
    private sealed record Leaf(
        IReadOnlyList<string> Path,
        string Expression,
        string Usage,
        string Returns,
        string Http,
        string Summary);

    private sealed record Section(string Title, string Description, List<Leaf> Leaves, List<string> Notes);

    private sealed class Model(JsonObject openApi, IReadOnlyList<GeneratedInstance> instances)
    {
        private readonly JsonObject schemas = openApi["components"]?["schemas"] as JsonObject ?? new JsonObject();
        private readonly JsonObject paths = openApi["paths"] as JsonObject ?? new JsonObject();

        private readonly List<Leaf> leaves = [];
        private readonly List<Section> sections = [];
        private readonly SortedSet<string> runtimeTypes = new(StringComparer.Ordinal) { "TenantClientOptions" };
        private readonly SortedSet<string> referenced = new(StringComparer.Ordinal);
        private readonly List<string> collections = [];
        private readonly List<string> forms = [];
        private readonly List<string> collectionDocs = [];
        private readonly List<string> formDocs = [];

        public void Build()
        {
            var top = new Section("Built in", "Available on every tenant.", [], []);
            sections.Add(top);
            Add(top, new Leaf(["media", "url"], "(assetId: string, variant?: string): string => http.mediaUrl(assetId, variant)",
                "api.media.url(assetId, variant?)", "string", "GET /api/media/{assetId}/{variant}",
                "URL of a media asset. Images: webp-320, webp-640, webp-960, webp-1280, webp-1920, thumb, original. Video: hls-master."));
            Add(top, new Leaf(["content"], "http.content",
                "api.content(instance, contentType).list(params?) / .get(slug)", "PagedResult / ContentItem", "GET /api/{instance}/{contentType}",
                "Untyped access to any collection by slug — what `collections` is for."));
            Add(top, new Leaf(["submitForm"], "http.submitForm",
                "api.submitForm(instance, formName, values)", "SubmissionResult", "POST /api/{instance}/forms/{name}",
                "Submit any form in `forms` without knowing its slug."));
            Add(top, new Leaf(["call"], "http.call",
                "api.call(method, path, { query?, body? })", "Promise<T>", "any",
                "Any operation in openapi.json, for one this file has no accessor for."));
            if (paths["/api/tags"]?["get"] is not null)
            {
                runtimeTypes.Add("TagIndex");
                Add(top, new Leaf(["tags", "list"],
                    "(params?: { contentType?: string; field?: string }): Promise<TagIndex> => http.listTags(params)",
                    "api.tags.list(params?)", "TagIndex", "GET /api/tags",
                    "Tags in use across every collection, most used first. Filter any list with { tag }."));
            }

            foreach (var instance in instances.OrderBy(i => i.Slug, StringComparer.Ordinal))
            {
                var section = new Section($"{instance.Name} (`{instance.Slug}`)", FirstLine(instance.Description), [], []);
                sections.Add(section);

                if (ReservedTopLevel.Contains(instance.Slug))
                {
                    // Emitting it would put two members of the same name in one object literal,
                    // which does not compile — and a site that does not build is worse than a
                    // missing shortcut. The generic accessor still reaches it.
                    section.Notes.Add($"The slug `{instance.Slug}` collides with a built-in member, so it has no typed accessor. Use `api.content(\"{instance.Slug}\", contentType)`.");
                    continue;
                }

                if (CapabilityPlugins.Contains(instance.PluginId))
                {
                    AddCapability(section, instance);
                    continue;
                }

                AddOperations(section, instance);
            }
        }

        private void Add(Section section, Leaf leaf)
        {
            // A plugin-chosen name can collide with a derived one. Keep the first and say so,
            // rather than emit an object literal that does not compile.
            if (leaves.Any(l => Collides(l.Path, leaf.Path)))
            {
                section.Notes.Add($"`{leaf.Http}` has no accessor: its name `{string.Join('.', leaf.Path)}` is already taken. Reach it with `api.call`.");
                return;
            }
            leaves.Add(leaf);
            section.Leaves.Add(leaf);
        }

        /// <summary>Equal paths, or one a strict prefix of the other (a member and a group of that name).</summary>
        private static bool Collides(IReadOnlyList<string> a, IReadOnlyList<string> b)
        {
            var n = Math.Min(a.Count, b.Count);
            for (var i = 0; i < n; i++)
            {
                if (!string.Equals(a[i], b[i], StringComparison.Ordinal))
                {
                    return false;
                }
            }
            return true;
        }

        private void AddCapability(Section section, GeneratedInstance instance)
        {
            var slug = Lit(instance.Slug);
            var s = instance.Slug;
            switch (instance.PluginId)
            {
                case "search":
                    runtimeTypes.Add("SearchParams");
                    runtimeTypes.Add("SearchResult");
                    Add(section, new Leaf([s, "search"], $"(params: SearchParams): Promise<SearchResult> => http.search({slug}, params)",
                        $"api.{Member(s)}.search({{ q, limit? }})", "SearchResult", $"GET /api/{s}/search",
                        "Full-text search across searchable content."));
                    break;
                case "visitor-auth":
                    Add(section, new Leaf([s, "auth"], $"http.visitorAuth({slug})",
                        $"api.{Member(s)}.auth.register(body) / .login(body) / .refresh(token) / .me()", "VisitorTokens / VisitorProfile",
                        $"POST /api/{s}/register|login|refresh, GET /api/{s}/me",
                        "Visitor accounts. Pass `visitorToken` to createTenantClient for `me()`."));
                    break;
                case "live-chat":
                    runtimeTypes.Add("ChatMessage");
                    Add(section, new Leaf([s, "chat", "history"],
                        $"(conversationId: string): Promise<ChatMessage[]> => http.chatHistory({slug}, conversationId)",
                        $"api.{Member(s)}.chat.history(conversationId)", "ChatMessage[]",
                        $"GET /api/{s}/chat/conversations/{{id}}/messages", "Messages of one chat conversation."));
                    break;
                case "analytics":
                    runtimeTypes.Add("AnalyticsEvent");
                    Add(section, new Leaf([s, "collect"], $"(event: AnalyticsEvent): Promise<void> => http.collect(event, {slug})",
                        $"api.{Member(s)}.collect(event)", "void", $"POST /api/{s}/collect",
                        "Record an analytics event. Prefer src/dcms: it handles consent."));
                    break;
            }
        }

        private void AddOperations(Section section, GeneratedInstance instance)
        {
            var prefix = $"/api/{instance.Slug}";
            foreach (var (key, node) in paths.OrderBy(p => p.Key, StringComparer.Ordinal))
            {
                if (node is not JsonObject item || !(key == prefix || key.StartsWith(prefix + "/", StringComparison.Ordinal)))
                {
                    continue;
                }
                var rest = key[prefix.Length..];

                foreach (var method in Methods)
                {
                    if (item[method] is not JsonObject op)
                    {
                        continue;
                    }
                    AddOperation(section, instance, key, rest, method, op);
                }
            }
        }

        private void AddOperation(Section section, GeneratedInstance instance, string fullPath, string rest, string method, JsonObject op)
        {
            var slug = instance.Slug;
            var segments = rest.Split('/', StringSplitOptions.RemoveEmptyEntries);
            var response = SuccessSchema(op);
            var summary = (op["summary"] as JsonValue)?.GetValue<string>() ?? string.Empty;
            var http = $"{method.ToUpperInvariant()} {fullPath}";

            // The shape every content plugin shares, kept on the runtime helpers it always used:
            // they know the paged envelope and the tag filters, and existing site code calls them.
            if (method == "get" && segments.Length == 1 && !segments[0].Contains('{')
                && ContentItemOfList(response) is { } listed)
            {
                var contentType = segments[0];
                var type = DataTypeName(listed);
                runtimeTypes.Add("ListParams");
                runtimeTypes.Add("PagedResult");
                Add(section, new Leaf(Named(op, [slug, contentType, "list"]),
                    $"(params?: ListParams): Promise<PagedResult<{type}>> => http.listContent<{type}>({Lit(slug)}, {Lit(contentType)}, params)",
                    $"api.{Member(slug)}.{Member(contentType)}.list({{ page?, pageSize?, tag? }})", $"PagedResult<{type}>", http, summary));
                AddCollection(instance, contentType, listed);
                return;
            }
            if (method == "get" && segments.Length == 2 && segments[1] == "{slug}" && ContentItemName(response) is { } single)
            {
                var contentType = segments[0];
                var type = DataTypeName(single);
                runtimeTypes.Add("ContentItem");
                Add(section, new Leaf(Named(op, [slug, contentType, "get"]),
                    $"(slug: string): Promise<ContentItem<{type}>> => http.getContent<{type}>({Lit(slug)}, {Lit(contentType)}, slug)",
                    $"api.{Member(slug)}.{Member(contentType)}.get(slug)", $"ContentItem<{type}>", http, summary));
                return;
            }

            var path = ClientPath(op) is { } declared
                ? [slug, .. declared]
                : Derived(slug, segments, method, op);

            var args = new List<string>();
            var usageArgs = new List<string>();
            var urlExpr = new StringBuilder("`");
            foreach (var part in fullPath.Split('/'))
            {
                if (part.Length == 0)
                {
                    continue;
                }
                urlExpr.Append('/');
                if (part.StartsWith('{') && part.EndsWith('}'))
                {
                    var name = Identifier(part[1..^1]);
                    args.Add($"{name}: string");
                    usageArgs.Add(name);
                    urlExpr.Append("${encodeURIComponent(").Append(name).Append(")}");
                }
                else
                {
                    urlExpr.Append(part.Replace("`", "\\`").Replace("${", "\\${"));
                }
            }
            urlExpr.Append('`');

            var options = new List<string>();
            if (op["requestBody"]?["content"]?["application/json"]?["schema"] is JsonObject bodySchema)
            {
                args.Add($"body: {TsType(bodySchema)}");
                usageArgs.Add("body");
                options.Add("body");
                if (instance.PluginId == "forms" && segments is ["forms", var formName])
                {
                    AddForm(instance, formName, path, bodySchema);
                }
            }

            var query = (op["parameters"] as JsonArray ?? [])
                .OfType<JsonObject>()
                .Where(p => (p["in"] as JsonValue)?.GetValue<string>() == "query")
                .ToList();
            if (query.Count > 0)
            {
                var anyRequired = query.Any(p => p["required"] is JsonValue r && r.GetValue<bool>());
                var fields = string.Join("; ", query.Select(p =>
                {
                    var name = (p["name"] as JsonValue)?.GetValue<string>() ?? "param";
                    var required = p["required"] is JsonValue r && r.GetValue<bool>();
                    return $"{PropertyKey(name)}{(required ? "" : "?")}: {TsType(p["schema"] as JsonObject ?? [])}";
                }));
                args.Add($"params{(anyRequired ? "" : "?")}: {{ {fields} }}");
                usageArgs.Add(anyRequired ? "params" : "params?");
                options.Add("query: params");
            }

            var returns = response is null ? "void" : TsType(response);
            var call = $"http.call<{returns}>({Lit(method.ToUpperInvariant())}, {urlExpr}{(options.Count > 0 ? $", {{ {string.Join(", ", options)} }}" : "")})";
            Add(section, new Leaf(path,
                $"({string.Join(", ", args)}): Promise<{returns}> => {call}",
                $"api.{string.Join('.', path.Select(Member))}({string.Join(", ", usageArgs)})", returns, http, summary));
        }

        /// <summary>A name for an operation its plugin did not name: its operation id, less the instance prefix.</summary>
        private static IReadOnlyList<string> Derived(string slug, string[] segments, string method, JsonObject op)
        {
            var operationId = (op["operationId"] as JsonValue)?.GetValue<string>();
            if (!string.IsNullOrEmpty(operationId))
            {
                var bare = operationId.StartsWith(slug + "_", StringComparison.Ordinal) ? operationId[(slug.Length + 1)..] : operationId;
                return [slug, Camel(bare)];
            }
            var named = segments.Where(s => !s.Contains('{')).Select(Camel).ToList();
            return [slug, .. named, method];
        }

        private static IReadOnlyList<string> Named(JsonObject op, IReadOnlyList<string> fallback) =>
            ClientPath(op) is { } declared ? [fallback[0], .. declared] : fallback;

        private static IReadOnlyList<string>? ClientPath(JsonObject op)
        {
            if (op["x-dcms-client"] is not JsonArray array || array.Count == 0)
            {
                return null;
            }
            var parts = array.Select(n => (n as JsonValue)?.GetValue<string>()).ToList();
            return parts.All(p => !string.IsNullOrEmpty(p)) ? parts.Select(p => p!).ToList() : null;
        }

        private static JsonObject? SuccessSchema(JsonObject op)
        {
            if (op["responses"] is not JsonObject responses)
            {
                return null;
            }
            foreach (var (status, response) in responses.OrderBy(r => r.Key, StringComparer.Ordinal))
            {
                if (status.StartsWith('2') && response?["content"]?["application/json"]?["schema"] is JsonObject schema)
                {
                    return schema;
                }
            }
            return null;
        }

        // ---- content collections ----------------------------------------------------------

        private static string? RefName(JsonObject? schema) =>
            (schema?["$ref"] as JsonValue)?.GetValue<string>() is { } r ? r.Split('/')[^1] : null;

        /// <summary>The component name of a content item: a schema whose <c>data</c> is the typed payload.</summary>
        private string? ContentItemName(JsonObject? schema) =>
            RefName(schema) is { } name && IsContentItem(name) ? name : null;

        private bool IsContentItem(string name) => schemas[name]?["properties"]?["data"] is JsonObject;

        /// <summary>For a paged envelope of content items, the item's component name.</summary>
        private string? ContentItemOfList(JsonObject? schema)
        {
            if (RefName(schema) is not { } name || schemas[name]?["properties"] is not JsonObject props)
            {
                return null;
            }
            return props["totalCount"] is not null && props["items"]?["items"] is JsonObject items ? ContentItemName(items) : null;
        }

        /// <summary>A content item's type is its data payload — what `ContentItem&lt;T&gt;` wraps.</summary>
        private string DataTypeName(string itemName)
        {
            referenced.Add(itemName);
            return Pascal(itemName);
        }

        private void AddCollection(GeneratedInstance instance, string contentType, string itemName)
        {
            var data = schemas[itemName]?["properties"]?["data"]?["properties"] as JsonObject ?? [];
            string? Find(params string[] names) => names.FirstOrDefault(n => data[n] is JsonObject);
            string? FirstWhere(Func<JsonObject, bool> test) =>
                data.FirstOrDefault(p => p.Value is JsonObject o && test(o)).Key;

            var title = Find("title", "name", "headline", "label", "caption")
                ?? FirstWhere(o => Str(o, "type") == "string" && !o.ContainsKey("format") && !o.ContainsKey("x-dcms-format"));
            var summary = Find("excerpt", "summary", "subtitle", "description", "tagline", "lead");
            var image = FirstWhere(o => Str(o, "x-dcms-media-category") == "image")
                ?? FirstWhere(o => Str(o["items"] as JsonObject, "x-dcms-media-category") == "image");
            var body = FirstWhere(o => Str(o, "x-dcms-format") is "richtext" or "markdown")
                ?? Find("body", "content", "text");
            var bodyFormat = body is null ? null : Str(data[body] as JsonObject, "x-dcms-format") ?? "text";
            var date = FirstWhere(o => Str(o, "format") is "date-time" or "date");

            var fields = new List<string>
            {
                $"instance: {Lit(instance.Slug)}",
                $"contentType: {Lit(contentType)}",
                $"label: {Lit(instance.Name)}",
                $"description: {Lit(FirstLine(instance.Description))}",
            };
            void Hint(string key, string? value)
            {
                if (value is not null)
                {
                    fields.Add($"{key}: {Lit(value)}");
                }
            }
            Hint("titleField", title == summary ? null : title);
            Hint("summaryField", summary);
            Hint("imageField", image);
            Hint("bodyField", body);
            Hint("bodyFormat", bodyFormat);
            Hint("dateField", date);
            collections.Add($"  {{ {string.Join(", ", fields)} }},");

            runtimeTypes.Add("CollectionInfo");
            collectionDocs.Add($"| {instance.Name} | `api.{Member(instance.Slug)}.{Member(contentType)}` | {string.Join(", ", data.Select(p => p.Key))} |");
        }

        private void AddForm(GeneratedInstance instance, string formName, IReadOnlyList<string> path, JsonObject bodySchema)
        {
            var resolved = RefName(bodySchema) is { } name ? schemas[name] as JsonObject : bodySchema;
            var props = resolved?["properties"] as JsonObject ?? [];
            var required = (resolved?["required"] as JsonArray)?.Select(n => (n as JsonValue)?.GetValue<string>()).ToHashSet() ?? [];
            var title = Str(resolved, "title") ?? formName;

            var fields = props.Select(p =>
            {
                var o = p.Value as JsonObject ?? [];
                var type = Str(o, "x-dcms-field") ?? Str(o, "type") switch
                {
                    "boolean" => "checkbox",
                    "number" or "integer" => "number",
                    _ => Str(o, "format") is "email" or "date" ? Str(o, "format")! : "text",
                };
                var parts = new List<string>
                {
                    $"name: {Lit(p.Key)}",
                    $"label: {Lit(Str(o, "description") ?? p.Key)}",
                    $"type: {Lit(type)}",
                    $"required: {(required.Contains(p.Key) ? "true" : "false")}",
                };
                if (o["maxLength"] is JsonValue max && max.TryGetValue<int>(out var length) && length > 0)
                {
                    parts.Add($"maxLength: {length}");
                }
                return $"{{ {string.Join(", ", parts)} }}";
            });

            runtimeTypes.Add("FormInfo");
            forms.Add($"  {{ instance: {Lit(instance.Slug)}, name: {Lit(formName)}, title: {Lit(title)}, fields: [{string.Join(", ", fields)}] }},");
            formDocs.Add($"| {title} | `api.{string.Join('.', path.Select(Member))}(body)` | {string.Join(", ", props.Select(p => required.Contains(p.Key) ? p.Key : p.Key + "?"))} |");
        }

        // ---- schema → TypeScript ----------------------------------------------------------

        private string TsType(JsonObject schema)
        {
            if (RefName(schema) is { } name)
            {
                if (IsContentItem(name))
                {
                    runtimeTypes.Add("ContentItem");
                    return $"ContentItem<{DataTypeName(name)}>";
                }
                if (ContentItemOfList(schema) is { } item)
                {
                    runtimeTypes.Add("PagedResult");
                    return $"PagedResult<{DataTypeName(item)}>";
                }
                referenced.Add(name);
                return Pascal(name);
            }

            var nullable = schema["nullable"] is JsonValue n && n.GetValue<bool>();
            var type = schema["type"] switch
            {
                JsonValue v => v.GetValue<string>(),
                JsonArray a => a.Select(x => (x as JsonValue)?.GetValue<string>()).FirstOrDefault(t => t != "null"),
                _ => null,
            };
            if (schema["type"] is JsonArray types && types.Any(t => (t as JsonValue)?.GetValue<string>() == "null"))
            {
                nullable = true;
            }

            var ts = type switch
            {
                "string" when schema.ContainsKey("x-dcms-media-category") => Runtime("MediaRef"),
                "string" when schema.ContainsKey("x-dcms-content-ref") => Runtime("ContentRef"),
                "string" when schema["enum"] is JsonArray values && values.Count > 0 =>
                    string.Join(" | ", values.Select(v => v is JsonValue sv && sv.TryGetValue<string>(out var s) ? Lit(s) : "string").Distinct()),
                "string" => "string",
                "integer" or "number" => "number",
                "boolean" => "boolean",
                "array" => ArrayOf(TsType(schema["items"] as JsonObject ?? [])),
                "object" or null when schema["properties"] is JsonObject props => InlineObject(schema, props),
                "object" when schema["additionalProperties"] is JsonObject extra => $"Record<string, {TsType(extra)}>",
                "object" => "Record<string, unknown>",
                _ => "unknown",
            };
            return nullable && ts != "unknown" ? $"{ts} | null" : ts;
        }

        private string Runtime(string name)
        {
            runtimeTypes.Add(name);
            return name;
        }

        private static string ArrayOf(string element) => element.Contains(' ') ? $"({element})[]" : $"{element}[]";

        private string InlineObject(JsonObject schema, JsonObject props)
        {
            var required = RequiredSet(schema);
            var members = props.Select(p => $"{PropertyKey(p.Key)}{(required.Contains(p.Key) ? "" : "?")}: {TsType(p.Value as JsonObject ?? [])}");
            return $"{{ {string.Join("; ", members)} }}";
        }

        private static HashSet<string> RequiredSet(JsonObject? schema) =>
            (schema?["required"] as JsonArray)?
                .Select(n => (n as JsonValue)?.GetValue<string>())
                .OfType<string>()
                .ToHashSet(StringComparer.Ordinal) ?? [];

        private string InterfaceBody(JsonObject schema)
        {
            var props = schema["properties"] as JsonObject ?? [];
            var required = RequiredSet(schema);
            var sb = new StringBuilder();
            foreach (var (name, node) in props)
            {
                var field = node as JsonObject ?? [];
                if (Str(field, "description") is { Length: > 0 } description)
                {
                    sb.AppendLine($"  /** {Comment(description)} */");
                }
                sb.AppendLine($"  {PropertyKey(name)}{(required.Contains(name) ? "" : "?")}: {TsType(field)};");
            }
            return sb.ToString();
        }

        // ---- rendering --------------------------------------------------------------------

        public string RenderTypes(string runtimeModule)
        {
            // Rendering a schema can reference further schemas, so walk until nothing new appears.
            var rendered = new SortedDictionary<string, string>(StringComparer.Ordinal);
            while (referenced.FirstOrDefault(r => !rendered.ContainsKey(r)) is { } next)
            {
                if (schemas[next] is not JsonObject schema)
                {
                    rendered[next] = $"export type {Pascal(next)} = unknown;\n";
                    continue;
                }
                if (IsContentItem(next))
                {
                    var data = schema["properties"]!["data"] as JsonObject ?? [];
                    rendered[next] = $"export interface {Pascal(next)} {{\n{InterfaceBody(data)}}}\n";
                }
                else if (schema["properties"] is JsonObject)
                {
                    rendered[next] = $"export interface {Pascal(next)} {{\n{InterfaceBody(schema)}}}\n";
                }
                else
                {
                    rendered[next] = $"export type {Pascal(next)} = {TsType(schema)};\n";
                }
            }

            var brands = UsedNames(string.Concat(rendered.Values), ["MediaRef", "ContentRef"]);

            var sb = new StringBuilder();
            sb.AppendLine(Header);
            if (brands.Count > 0)
            {
                sb.AppendLine($"import type {{ {string.Join(", ", brands)} }} from '{runtimeModule}';");
            }
            sb.AppendLine();
            if (rendered.Count == 0)
            {
                // Keep the module valid even when the tenant has no typed operations yet.
                sb.AppendLine("export {};");
                return sb.ToString();
            }
            foreach (var body in rendered.Values)
            {
                sb.AppendLine(body);
            }
            return sb.ToString().TrimEnd() + "\n";
        }

        public string RenderIndex(string runtimeModule)
        {
            var body = new StringBuilder();
            body.AppendLine("  const http = createHttpCore(options);");
            body.AppendLine("  return {");
            RenderTree(body, leaves, 0, "    ");
            body.AppendLine("  };");

            var sb = new StringBuilder();
            sb.AppendLine(Header);
            var code = new StringBuilder();
            code.AppendLine("/** A typed client for this tenant's content API. See API.md beside this file. */");
            code.AppendLine("export function createTenantClient(options: TenantClientOptions = {}) {");
            code.Append(body);
            code.AppendLine("}");
            code.AppendLine();
            code.AppendLine("export type TenantClient = ReturnType<typeof createTenantClient>;");
            code.AppendLine();
            code.AppendLine("/** Every content collection this tenant publishes, with hints for which field plays which role. */");
            code.AppendLine("export const collections: readonly CollectionInfo[] = [");
            foreach (var c in collections)
            {
                code.AppendLine(c);
            }
            code.AppendLine("];");
            code.AppendLine();
            code.AppendLine("/** Every visitor form this tenant accepts, with its fields. */");
            code.AppendLine("export const forms: readonly FormInfo[] = [");
            foreach (var f in forms)
            {
                code.AppendLine(f);
            }
            code.AppendLine("];");
            var text = code.ToString();

            // Imports are read off the code that was written rather than tracked while writing it:
            // a list kept by hand is one missed `Add` away from a site that does not compile.
            sb.AppendLine("import {");
            sb.AppendLine("  createHttpCore,");
            foreach (var type in UsedNames(text, RuntimeTypeNames))
            {
                sb.AppendLine($"  type {type},");
            }
            sb.AppendLine($"}} from '{runtimeModule}';");
            var local = UsedNames(text, referenced.Select(Pascal).Distinct());
            if (local.Count > 0)
            {
                sb.AppendLine($"import type {{ {string.Join(", ", local)} }} from './types';");
            }
            sb.AppendLine();
            sb.Append(text);
            sb.AppendLine();
            // Re-export the shared runtime types and the generated types so this module is the
            // single import surface (e.g. `import type { ContentItem } from './api'`).
            // ApiError is a value — `instanceof` needs the class, not its type.
            sb.AppendLine("export { ApiError } from './runtime';");
            sb.AppendLine("export type * from './runtime';");
            sb.AppendLine("export type * from './types';");
            return sb.ToString();
        }

        private static void RenderTree(StringBuilder sb, IReadOnlyList<Leaf> items, int depth, string indent)
        {
            foreach (var group in items.GroupBy(l => l.Path[depth]))
            {
                var name = group.Key;
                var here = group.ToList();
                if (here.Count == 1 && here[0].Path.Count == depth + 1)
                {
                    sb.AppendLine($"{indent}{PropertyKey(name)}: {here[0].Expression},");
                    continue;
                }
                // Groups are quoted, as instance slugs always have been: a slug is not an identifier.
                sb.AppendLine($"{indent}{(depth == 0 && ReservedTopLevel.Contains(name) ? name : Lit(name))}: {{");
                RenderTree(sb, here.Where(l => l.Path.Count > depth + 1).ToList(), depth + 1, indent + "  ");
                sb.AppendLine($"{indent}}},");
            }
        }

        public string RenderMarkdown()
        {
            var sb = new StringBuilder();
            sb.AppendLine("# Tenant API");
            sb.AppendLine();
            sb.AppendLine("Generated by DCMS from this tenant's plugins. **Do not edit** — this folder is rewritten");
            sb.AppendLine("whenever the tenant's plugins change. Read this file instead of `openapi.json`: it lists");
            sb.AppendLine("every call the typed client offers.");
            sb.AppendLine();
            sb.AppendLine("```ts");
            sb.AppendLine("import { createTenantClient, collections, forms } from './api'; // path relative to src/");
            sb.AppendLine("const api = createTenantClient({ baseUrl: import.meta.env.VITE_API_BASE_URL ?? '' });");
            sb.AppendLine("```");
            sb.AppendLine();
            sb.AppendLine("Lists return `{ items, page, pageSize, totalCount }`; each item is `{ id, slug, data, publishedAt }`");
            sb.AppendLine("with the typed fields under `data`. Media fields hold asset ids — pass them to `api.media.url`.");
            sb.AppendLine("Failed calls throw `ApiError` with `status` and `body`. Exact field types are in `types.ts`.");

            if (collectionDocs.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine("## Collections");
                sb.AppendLine();
                sb.AppendLine("Also exported as `collections`, with `titleField` / `imageField` / `bodyField` hints.");
                sb.AppendLine();
                sb.AppendLine("| Collection | Accessor | Fields of `data` |");
                sb.AppendLine("| --- | --- | --- |");
                foreach (var line in collectionDocs)
                {
                    sb.AppendLine(line);
                }
            }
            if (formDocs.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine("## Forms");
                sb.AppendLine();
                sb.AppendLine("Also exported as `forms`, with labels and field types. Returns `{ submissionId, message }`.");
                sb.AppendLine();
                sb.AppendLine("| Form | Call | Fields (`?` optional) |");
                sb.AppendLine("| --- | --- | --- |");
                foreach (var line in formDocs)
                {
                    sb.AppendLine(line);
                }
            }

            sb.AppendLine();
            sb.AppendLine("## Calls");
            foreach (var section in sections)
            {
                if (section.Leaves.Count == 0 && section.Notes.Count == 0)
                {
                    continue;
                }
                sb.AppendLine();
                sb.AppendLine($"### {section.Title}");
                if (section.Description.Length > 0)
                {
                    sb.AppendLine();
                    sb.AppendLine(section.Description);
                }
                sb.AppendLine();
                foreach (var leaf in section.Leaves)
                {
                    var summary = leaf.Summary.Length > 0 ? $" — {leaf.Summary}" : string.Empty;
                    sb.AppendLine($"- `{leaf.Usage}` → `{leaf.Returns}`{summary} ({leaf.Http})");
                }
                foreach (var note in section.Notes)
                {
                    sb.AppendLine($"- ⚠ {note}");
                }
            }
            return sb.ToString();
        }
    }

    /// <summary>Every type the runtime exports that generated code may name.</summary>
    private static readonly string[] RuntimeTypeNames =
    [
        "AnalyticsEvent", "ChatMessage", "CollectionInfo", "ContentItem", "ContentRef", "FormInfo",
        "ListParams", "MediaRef", "PagedResult", "SearchParams", "SearchResult", "TagIndex", "TenantClientOptions",
    ];

    /// <summary>The candidates that appear in <paramref name="code"/> as whole words, sorted.</summary>
    private static List<string> UsedNames(string code, IEnumerable<string> candidates) =>
        candidates
            .Where(name => System.Text.RegularExpressions.Regex.IsMatch(code, $@"\b{System.Text.RegularExpressions.Regex.Escape(name)}\b"))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToList();

    private const string Header = "// AUTO-GENERATED by DCMS from this tenant's plugins. Do not edit: it is regenerated when they change.";

    private static string? Str(JsonObject? o, string key) =>
        o?[key] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

    private static string FirstLine(string? text) =>
        (text ?? string.Empty).Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault() ?? string.Empty;

    private static string Comment(string text) => FirstLine(text).Replace("*/", "*\\/");

    private static string Lit(string value) => JsonSerializer.Serialize(value);

    /// <summary>A member as written after a dot in usage docs.</summary>
    private static string Member(string name) => IsSafeIdentifier(name) ? name : $"[{Lit(name)}]";

    private static string PropertyKey(string name) => IsSafeIdentifier(name) ? name : Lit(name);

    private static readonly HashSet<string> ReservedWords = new(StringComparer.Ordinal)
    {
        "break", "case", "catch", "class", "const", "continue", "debugger", "default", "delete", "do",
        "else", "enum", "export", "extends", "false", "finally", "for", "function", "if", "import", "in",
        "instanceof", "new", "null", "return", "super", "switch", "this", "throw", "true", "try",
        "typeof", "var", "void", "while", "with", "yield", "let", "static", "implements", "interface",
        "package", "private", "protected", "public", "await", "http", "options", "params", "body",
    };

    /// <summary>A safe parameter name for a path placeholder.</summary>
    private static string Identifier(string raw)
    {
        var camel = Camel(raw);
        if (camel.Length == 0)
        {
            camel = "value";
        }
        return ReservedWords.Contains(camel) ? camel + "Value" : camel;
    }

    private static string Camel(string raw)
    {
        var pascal = Pascal(raw);
        return pascal.Length == 0 ? pascal : char.ToLowerInvariant(pascal[0]) + pascal[1..];
    }

    private static string Pascal(string raw)
    {
        var sb = new StringBuilder();
        var upperNext = true;
        foreach (var c in raw)
        {
            if (char.IsLetterOrDigit(c))
            {
                sb.Append(upperNext ? char.ToUpperInvariant(c) : c);
                upperNext = false;
            }
            else
            {
                upperNext = true;
            }
        }

        var result = sb.ToString();
        if (result.Length == 0)
        {
            return "Type";
        }
        return char.IsDigit(result[0]) ? "_" + result : result;
    }

    private static bool IsSafeIdentifier(string name)
    {
        if (name.Length == 0 || !(char.IsLetter(name[0]) || name[0] is '_' or '$'))
        {
            return false;
        }
        foreach (var c in name)
        {
            if (!(char.IsLetterOrDigit(c) || c is '_' or '$'))
            {
                return false;
            }
        }
        return true;
    }
}
