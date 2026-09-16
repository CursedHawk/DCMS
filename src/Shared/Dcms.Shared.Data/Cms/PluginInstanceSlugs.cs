namespace Dcms.Shared.Data.Cms;

/// <summary>
/// Which slugs a plugin instance may take.
///
/// <para>An instance's slug is the first path segment of its whole public API
/// (<c>/api/{slug}/…</c>) and the name of its member on the generated site client
/// (<c>api.{slug}</c>). Both of those namespaces already hold names of their own, and an instance
/// that takes one of them loses: ASP.NET routing prefers a literal segment, so
/// <c>/api/analytics/status</c> never reaches an instance called <c>analytics</c>; and the client
/// cannot hold two members of one name, so an instance called <c>content</c> gets no typed
/// accessor at all.</para>
///
/// <para>Refused at creation, where the author can still pick another name, instead of explained
/// after the fact in a site's <c>API.md</c>. Slugs cannot be renamed later, so an instance created
/// before this rule existed keeps its slug; the client generator still handles it.</para>
///
/// <para>The route half is checked against content-api's real endpoint table by a test, so a new
/// literal route under <c>/api/</c> that is not listed here fails the build.</para>
/// </summary>
public static class PluginInstanceSlugs
{
    /// <summary>Each reserved slug, with what already owns it.</summary>
    public static readonly IReadOnlyDictionary<string, string> Reserved =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            // content-api's own routes under /api/.
            ["analytics"] = "/api/analytics/…",
            ["collect"] = "/api/collect",
            ["media"] = "/api/media/…",
            ["openapi"] = "/api/openapi",
            ["tags"] = "/api/tags",
            // Built-in members of the generated site client.
            ["call"] = "api.call() on the generated site client",
            ["content"] = "api.content() on the generated site client",
        };

    public static bool IsWellFormed(string value) =>
        value.Length > 0
        && value.All(c => char.IsAsciiLetterLower(c) || char.IsAsciiDigit(c) || c == '-')
        && !value.StartsWith('-') && !value.EndsWith('-');

    /// <summary>Why <paramref name="slug"/> cannot be used, or <c>null</c> if it can.</summary>
    public static string? Problem(string? slug)
    {
        if (string.IsNullOrWhiteSpace(slug) || !IsWellFormed(slug))
        {
            return "slug must be kebab-case.";
        }
        return Reserved.TryGetValue(slug, out var owner)
            ? $"slug \"{slug}\" is reserved: it is already used by {owner}."
            : null;
    }
}
