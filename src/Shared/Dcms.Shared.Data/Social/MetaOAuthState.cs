namespace Dcms.Shared.Data.Social;

/// <summary>
/// A pending OAuth authorization, held for the round trip to Meta and back.
///
/// <para>A row rather than a cookie because the callback is anonymous and can land on
/// any replica: it arrives with no bearer token and no tenant header, so this row is
/// the only thing that says which tenant started the flow. That makes it the whole of
/// the CSRF defence — the lookup key is the SHA-256 of a random token that only ever
/// existed in the redirect URL, and a row is single-use and short-lived.</para>
///
/// <para>Because the callback has no tenant context, reads of this table must use
/// <c>IgnoreQueryFilters()</c> and then act under the <c>TenantId</c> found here.</para>
/// </summary>
public sealed class MetaOAuthState : TenantEntity
{
    /// <summary>SHA-256 of the state token. The token itself is never stored.</summary>
    public string StateHash { get; set; } = string.Empty;

    public MetaProvider Provider { get; set; }

    /// <summary>The plugin instance being connected, when the flow started from one.</summary>
    public Guid? PluginInstanceId { get; set; }

    public Guid InitiatedBy { get; set; }

    /// <summary>Relative admin-SPA path to return the browser to. Validated as relative on use.</summary>
    public string? ReturnPath { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset ExpiresAt { get; set; }
    public DateTimeOffset? ConsumedAt { get; set; }
}
