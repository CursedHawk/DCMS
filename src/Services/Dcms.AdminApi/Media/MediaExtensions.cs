using Dcms.Shared.Contracts.Events;

namespace Dcms.AdminApi.Media;

internal static class MediaExtensions
{
    public static string ToExtension(string contentType) => contentType switch
    {
        "image/jpeg" => ".jpg",
        "image/png" => ".png",
        "image/gif" => ".gif",
        "image/webp" => ".webp",
        "video/mp4" => ".mp4",
        "audio/mpeg" => ".mp3",
        "audio/wav" => ".wav",
        "audio/ogg" => ".ogg",
        "application/pdf" => ".pdf",
        "application/zip" => ".zip",
        _ => ".bin",
    };

    public static string ProcessSubject(MediaCategory category) => category switch
    {
        MediaCategory.Image => Dcms.Shared.Contracts.Messaging.Subjects.MediaProcessImage,
        MediaCategory.Video => Dcms.Shared.Contracts.Messaging.Subjects.MediaProcessVideo,
        MediaCategory.Audio => Dcms.Shared.Contracts.Messaging.Subjects.MediaProcessAudio,
        _ => string.Empty,
    };
}
