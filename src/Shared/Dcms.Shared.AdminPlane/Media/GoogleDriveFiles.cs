using System.Net.Http.Headers;

namespace Dcms.AdminApi.Media;

/// <summary>Talking to Drive about one file: shared by the import endpoint (which checks the picked files) and the import job (which downloads them).</summary>
public static class GoogleDriveFiles
{
    public const string GoogleAppsPrefix = "application/vnd.google-apps.";

    /// <summary>
    /// Google-native files have no bytes of their own, so Drive exports them. PDF is a type
    /// the library already stores; a Drawing goes to PNG so it gets the webp ladder like any
    /// image. Null for anything else Google-native (folders, forms, sites…), which cannot be
    /// imported.
    /// </summary>
    public static (string MimeType, string Extension)? ExportFor(string mimeType) => mimeType switch
    {
        "application/vnd.google-apps.document"
            or "application/vnd.google-apps.spreadsheet"
            or "application/vnd.google-apps.presentation" => ("application/pdf", ".pdf"),
        "application/vnd.google-apps.drawing" => ("image/png", ".png"),
        _ => null,
    };

    public static Task<HttpResponseMessage> SendAsync(HttpClient http, Uri uri, string token, CancellationToken ct)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
    }


    public sealed record DriveFile(string? Name, string? MimeType, string? Size)
    {
        /// <summary>Drive sends size as a string, and omits it for Google-native files.</summary>
        public long SizeBytes => long.TryParse(Size, out var n) ? n : 0;
    }
}
