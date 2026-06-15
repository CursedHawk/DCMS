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
}
