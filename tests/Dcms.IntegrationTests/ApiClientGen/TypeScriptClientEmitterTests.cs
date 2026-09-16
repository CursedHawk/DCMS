extern alias AdminApiApp;

using System.IO.Compression;
using AdminApiApp::Dcms.AdminApi.ApiClientGen;

namespace Dcms.IntegrationTests.ApiClientGen;

/// <summary>
/// The TypeScript client emitter, over a document the real assembler built from real plugins
/// (<see cref="SampleTenant"/>). Container-free:
/// <c>dotnet test --filter FullyQualifiedName~ApiClientGen</c>
/// </summary>
public class TypeScriptClientEmitterTests
{
    private static IReadOnlyDictionary<string, string> Emit(bool tagging = true)
    {
        var api = SampleTenant.Snapshot(tagging);
        return TypeScriptClientEmitter.Emit(api.Document, api.Instances);
    }

    [Fact]
    public void Types_a_content_item_as_its_data_payload()
    {
        var types = Emit()["src/types.ts"];

        Assert.Contains("export interface NewsPost {", types);
        Assert.Contains("title: string;", types);
        Assert.Contains("coverImage?: MediaRef;", types);
        Assert.Contains("import type { MediaRef } from '@dcms/api-client';", types);
        // The paged envelope is the runtime's PagedResult<T>, not a second interface.
        Assert.DoesNotContain("NewsPostList", types);
    }

    [Fact]
    public void Keeps_the_content_list_and_get_signatures_existing_sites_call()
    {
        var index = Emit()["src/index.ts"];

        Assert.Contains("\"news\": {", index);
        Assert.Contains("\"post\": {", index);
        Assert.Contains("list: (params?: ListParams): Promise<PagedResult<NewsPost>> => http.listContent<NewsPost>(\"news\", \"post\", params)", index);
        Assert.Contains("get: (slug: string): Promise<ContentItem<NewsPost>> => http.getContent<NewsPost>(\"news\", \"post\", slug)", index);
        Assert.Contains("export type TenantClient = ReturnType<typeof createTenantClient>;", index);
    }

    [Fact]
    public void Emits_a_typed_form_submission_named_by_the_plugin()
    {
        // The old emitter only recognised content-shaped paths, so a tenant's forms were not in
        // its client at all.
        var files = Emit();

        Assert.Contains("\"forms\": {", files["src/index.ts"]);
        Assert.Contains("\"contact\": {", files["src/index.ts"]);
        Assert.Contains("submit: (body: EnquiriesContactSubmission):", files["src/index.ts"]);
        Assert.Contains("http.call<", files["src/index.ts"]);
        Assert.Contains("\"POST\", `/api/enquiries/forms/contact`, { body }", files["src/index.ts"]);

        var types = files["src/types.ts"];
        Assert.Contains("export interface EnquiriesContactSubmission {", types);
        Assert.Contains("name: string;", types);
        Assert.Contains("message?: string;", types);
        Assert.Contains("newsletter?: boolean;", types);
    }

    [Fact]
    public void Emits_branding_as_a_single_get_not_a_paged_list()
    {
        // Before: api.brand.branding.list() → PagedResult, and .get(slug) → a URL that does not exist.
        var index = Emit()["src/index.ts"];

        Assert.Contains("\"branding\": {", index);
        Assert.Contains("get: (): Promise<BrandBranding> => http.call<BrandBranding>(\"GET\", `/api/brand/branding`)", index);
        Assert.DoesNotContain("PagedResult<BrandBranding>", index);
        Assert.Contains("export interface BrandBranding {", Emit()["src/types.ts"]);
    }

    [Fact]
    public void Includes_the_tag_index_exactly_when_the_document_does()
    {
        Assert.Contains("tags: {", Emit(tagging: true)["src/index.ts"]);
        Assert.DoesNotContain("tags: {", Emit(tagging: false)["src/index.ts"]);
    }

    [Fact]
    public void Keeps_runtime_helpers_for_plugins_whose_endpoints_are_not_in_the_document()
    {
        var index = Emit()["src/index.ts"];

        Assert.Contains("search: (params: SearchParams): Promise<SearchResult> => http.search(\"find\", params)", index);
        Assert.Contains("auth: http.visitorAuth(\"members\")", index);
        Assert.Contains("history: (conversationId: string): Promise<ChatMessage[]> => http.chatHistory(\"support\", conversationId)", index);
        Assert.Contains("collect: (event: AnalyticsEvent): Promise<void> => http.collect(event, \"stats\")", index);
        Assert.Contains("url: (assetId: string, variant?: string): string => http.mediaUrl(assetId, variant)", index);
        // Analytics' own `/collect` operation is served by the helper, not emitted a second time.
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(index, "collect:"));
    }

    [Fact]
    public void Imports_exactly_the_runtime_types_the_code_names()
    {
        var index = Emit()["src/index.ts"];
        var imports = index[..index.IndexOf("} from './runtime';", StringComparison.Ordinal)];

        foreach (var used in new[] { "ListParams", "PagedResult", "ContentItem", "SearchParams", "ChatMessage", "AnalyticsEvent", "CollectionInfo", "FormInfo", "TagIndex" })
        {
            Assert.Contains($"type {used},", imports);
        }
        Assert.Contains("export { ApiError } from './runtime';", index);
    }

    [Fact]
    public void Describes_collections_by_the_role_each_field_plays()
    {
        var index = Emit()["src/index.ts"];

        Assert.Contains("export const collections: readonly CollectionInfo[] = [", index);
        Assert.Contains("instance: \"news\", contentType: \"post\", label: \"News\"", index);
        Assert.Contains("titleField: \"title\"", index);
        Assert.Contains("summaryField: \"excerpt\"", index);
        Assert.Contains("imageField: \"coverImage\"", index);
        Assert.Contains("bodyField: \"body\", bodyFormat: \"richtext\"", index);
    }

    [Fact]
    public void Describes_forms_with_the_field_types_the_tenant_configured()
    {
        var index = Emit()["src/index.ts"];

        Assert.Contains("export const forms: readonly FormInfo[] = [", index);
        Assert.Contains("instance: \"enquiries\", name: \"contact\", title: \"Contact us\"", index);
        // A textarea and a one-line input are the same JSON type; only x-dcms-field tells them apart.
        Assert.Contains("name: \"message\", label: \"Message\", type: \"textarea\", required: false, maxLength: 4000", index);
        Assert.Contains("name: \"email\", label: \"Email\", type: \"email\", required: true", index);
        Assert.Contains("name: \"newsletter\", label: \"Send me the newsletter\", type: \"checkbox\", required: false", index);
    }

    [Fact]
    public void Writes_a_compact_reference_of_every_call()
    {
        var doc = Emit()["src/API.md"];

        Assert.Contains("api.news.post.list(", doc);
        Assert.Contains("api.enquiries.forms.contact.submit(body)", doc);
        Assert.Contains("api.brand.branding.get()", doc);
        Assert.Contains("api.find.search(", doc);
        Assert.Contains("api.tags.list(", doc);
        Assert.Contains("## Collections", doc);
        Assert.Contains("## Forms", doc);
        // The point of the file: it is a fraction of the document it summarises.
        Assert.True(doc.Length * 4 < SampleTenant.Snapshot().Json.Length, $"API.md is {doc.Length} chars");
    }

    [Fact]
    public void Leaves_out_an_instance_whose_slug_would_not_compile_and_says_why()
    {
        // `media` is a built-in member; two members of one name in an object literal is a type error.
        var api = SampleTenant.Snapshot(rename: slug => slug == "news" ? "media" : slug);
        var files = TypeScriptClientEmitter.Emit(api.Document, api.Instances);

        Assert.DoesNotContain("\"media\": {", files["src/index.ts"]);
        Assert.Contains("collides with a built-in member", files["src/API.md"]);
    }

    [Fact]
    public void Packs_a_self_contained_client_zip()
    {
        var api = SampleTenant.Snapshot();
        var zip = ClientPackageBuilder.Build(api.Document, api.Instances);

        using var archive = new ZipArchive(new MemoryStream(zip), ZipArchiveMode.Read);
        var entries = archive.Entries.Select(e => e.FullName.Replace('\\', '/')).ToHashSet();

        Assert.Contains("src/index.ts", entries);
        Assert.Contains("src/types.ts", entries);
        Assert.Contains("src/API.md", entries);
        Assert.Contains("src/runtime/index.ts", entries);
        Assert.Contains("src/runtime/types.ts", entries);
        Assert.Contains("package.json", entries);
    }
}
