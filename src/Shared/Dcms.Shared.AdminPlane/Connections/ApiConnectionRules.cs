using System.Text.RegularExpressions;
using Dcms.Shared.Data.Cms;

namespace Dcms.AdminApi.Connections;

/// <summary>
/// What an external API connection may be (Mode D backlog #124). Kept strict because every rule
/// here is something admin-api would otherwise send, with a tenant's credential, to a host the
/// tenant chose: the address is https, the operations are paths on that one host, and the
/// credential travels only as the kind of header or parameter the tenant declared.
/// </summary>
public static partial class ApiConnectionRules
{
    /// <summary>The Transit key the credentials are encrypted under — granted to admin-api only.</summary>
    public const string TransitKey = "dcms-api-connections";
    public const int MaxOperations = 10;
    public const int MaxBodyBytes = 512 * 1024;
    public const int MinRefreshMinutes = 15;
    public const int MaxRefreshMinutes = 24 * 60;
    public static readonly string[] AuthKinds = ["none", "bearer", "header", "query"];

    [GeneratedRegex("^[A-Za-z0-9_-]{1,128}$")]
    private static partial Regex AuthNamePattern();

    // A path, optionally a query: no scheme, host, fragment, whitespace or dot segments.
    [GeneratedRegex(@"^/(?!/)[A-Za-z0-9\-._~%!$&'()*+,;=:@/]*(\?[A-Za-z0-9\-._~%!$&'()*+,;=:@/?]*)?$")]
    private static partial Regex OperationPattern();

    /// <summary>Header names a credential may not be sent as: they would change the request itself.</summary>
    private static readonly HashSet<string> ReservedHeaders = new(StringComparer.OrdinalIgnoreCase)
    {
        "host", "content-length", "content-type", "transfer-encoding", "connection", "cookie", "accept",
    };

    /// <summary>The first thing wrong with a connection, or null.</summary>
    public static string? Problem(string slug, string name, string baseUrl, string authKind, string? authName,
        IReadOnlyList<string> operations, int refreshMinutes, bool allowLocal)
    {
        if (slug.Length > 48 || !PluginInstanceSlugs.IsWellFormed(slug))
        {
            return "slug must be kebab-case, at most 48 characters.";
        }
        if (string.IsNullOrWhiteSpace(name) || name.Length > 120)
        {
            return "name is required, at most 120 characters.";
        }
        if (BaseUrlProblem(baseUrl, allowLocal) is { } urlProblem)
        {
            return urlProblem;
        }
        if (!AuthKinds.Contains(authKind))
        {
            return $"authKind must be one of {string.Join(", ", AuthKinds)}.";
        }
        if (authKind is "header" or "query")
        {
            if (authName is null || !AuthNamePattern().IsMatch(authName))
            {
                return "authName must name the header or query parameter the key goes in (letters, digits, - and _).";
            }
            if (authKind == "header" && ReservedHeaders.Contains(authName))
            {
                return $"the key cannot be sent as the {authName} header.";
            }
        }
        if (operations.Count is 0 or > MaxOperations)
        {
            return $"list 1 to {MaxOperations} operations.";
        }
        foreach (var operation in operations)
        {
            if (OperationProblem(operation) is { } opProblem)
            {
                return $"operation “{operation}”: {opProblem}";
            }
        }
        if (operations.Distinct(StringComparer.Ordinal).Count() != operations.Count)
        {
            return "an operation is listed twice.";
        }
        if (refreshMinutes is < MinRefreshMinutes or > MaxRefreshMinutes)
        {
            return $"refreshMinutes must be {MinRefreshMinutes} to {MaxRefreshMinutes}.";
        }
        return null;
    }

    public static string? BaseUrlProblem(string baseUrl, bool allowLocal)
    {
        if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var uri) || baseUrl.Length > 2048)
        {
            return "baseUrl must be an absolute URL.";
        }
        if (uri.Scheme != Uri.UriSchemeHttps && !(allowLocal && uri.Scheme == Uri.UriSchemeHttp))
        {
            return "baseUrl must be https.";
        }
        if (!string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment))
        {
            return "baseUrl may not carry credentials, a query or a fragment — the key goes in the auth settings.";
        }
        return null;
    }

    public static string? OperationProblem(string operation)
    {
        if (operation.Length > 512 || !OperationPattern().IsMatch(operation))
        {
            return "must be a path starting with / (and optionally ?query), with no host or fragment.";
        }
        var path = operation.Split('?')[0];
        if (path.Split('/').Any(s => s is "." or ".." || s.Contains("%2e", StringComparison.OrdinalIgnoreCase) || s.Contains("%2f", StringComparison.OrdinalIgnoreCase)))
        {
            return "may not contain . or .. segments.";
        }
        return null;
    }

    /// <summary>
    /// The URL an operation is fetched from — and a last check that it is still on the base URL's
    /// host, so no operation string can point the credential anywhere else.
    /// </summary>
    public static Uri Target(string baseUrl, string operation)
    {
        var baseUri = new Uri(baseUrl);
        var target = new Uri(baseUrl.TrimEnd('/') + operation, UriKind.Absolute);
        if (target.Scheme != baseUri.Scheme || target.Host != baseUri.Host || target.Port != baseUri.Port)
        {
            throw new InvalidOperationException("operation leaves the connection's host.");
        }
        return target;
    }
}
