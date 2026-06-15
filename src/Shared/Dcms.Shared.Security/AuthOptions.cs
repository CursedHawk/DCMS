namespace Dcms.Shared.Security;

/// <summary>
/// Bearer-token validation settings for a resource server. Authority is the
/// identity service base URL; Audience is this service's resource name (the
/// OpenIddict scope's resource). RequireHttps is off in dev compose.
/// </summary>
public sealed class AuthOptions
{
    public const string SectionName = "Auth";

    public string Authority { get; set; } = "http://localhost:5001/";

    /// <summary>
    /// Optional discovery-document URL. When the identity service is reachable
    /// at a different address internally (compose) than the public issuer, set
    /// this to the internal .well-known/openid-configuration URL while
    /// <see cref="Issuer"/> stays the public value used to sign/validate tokens.
    /// </summary>
    public string? MetadataAddress { get; set; }

    /// <summary>Expected token issuer. Defaults to <see cref="Authority"/>.</summary>
    public string? Issuer { get; set; }

    public string Audience { get; set; } = string.Empty;
    public bool RequireHttpsMetadata { get; set; }
}

/// <summary>
/// Client-credentials settings for outbound service-to-service calls.
/// </summary>
public sealed class ServiceClientOptions
{
    public const string SectionName = "ServiceClient";

    public string TokenEndpoint { get; set; } = "http://localhost:5001/connect/token";
    public string ClientId { get; set; } = string.Empty;
    public string ClientSecret { get; set; } = string.Empty;
}
