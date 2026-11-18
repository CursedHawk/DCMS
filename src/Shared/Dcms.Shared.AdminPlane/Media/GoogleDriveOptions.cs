namespace Dcms.AdminApi.Media;

/// <summary>
/// The platform's Google Cloud app, as the Drive picker in the console needs it. None of it is
/// secret — the picker runs in the browser — but it is per-environment, so it lives in Vault
/// (<c>secret/dcms/admin-api</c>, <c>GoogleDrive__*</c>) like every other value that differs
/// between dev and prod. Optional: unset, the console simply does not offer Drive.
/// </summary>
public sealed class GoogleDriveOptions
{
    public const string SectionName = "GoogleDrive";

    /// <summary>OAuth client id. May be the identity SSO client, with the console origin added
    /// to its authorized JavaScript origins.</summary>
    public string? ClientId { get; set; }

    /// <summary>Browser API key, restricted to the Picker API and the console's referrers.</summary>
    public string? ApiKey { get; set; }

    /// <summary>The Cloud project number. A drive.file grant made in the picker belongs to this
    /// project, which is what lets admin-api download the picked file with the same token.</summary>
    public string? AppId { get; set; }

    /// <summary>Overridden only by tests, which point it at a stub on loopback.</summary>
    public string ApiBase { get; set; } = "https://www.googleapis.com/drive/v3/";

    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(ClientId) && !string.IsNullOrWhiteSpace(ApiKey) && !string.IsNullOrWhiteSpace(AppId);
}
