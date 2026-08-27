namespace Dcms.Shared.Storage;

public sealed class StorageOptions
{
    public const string SectionName = "Storage";

    public string Endpoint { get; set; } = "localhost:9000";
    public string AccessKey { get; set; } = string.Empty;
    public string SecretKey { get; set; } = string.Empty;
    public bool UseSsl { get; set; }
    public string MediaBucket { get; set; } = "dcms-media";
    public string SitesBucket { get; set; } = "dcms-sites";
    public string BuildLogsBucket { get; set; } = "dcms-build-logs";

    /// <summary>
    /// The bucket the health check probes. Defaults to <see cref="MediaBucket"/>, which is
    /// right for every service that holds the root credentials.
    ///
    /// <para>site-builder does not. It connects with a service account scoped to
    /// <c>dcms-sites</c> and <c>dcms-build-logs</c> — deliberately, so a leaked builder key
    /// cannot touch tenant media — and probing <c>dcms-media</c> from there returns
    /// <c>AccessDenied</c>. That is a correct answer to the wrong question, and it made
    /// site-builder's <c>/health</c> report Unhealthy permanently. Nothing noticed, because
    /// the container healthcheck polls <c>/health/live</c>, which runs no checks at all.</para>
    /// </summary>
    public string? HealthBucket { get; set; }
}
