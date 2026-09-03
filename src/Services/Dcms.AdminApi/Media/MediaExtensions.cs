using Dcms.Shared.Contracts.Events;
using Dcms.Shared.Media;

namespace Dcms.AdminApi.Media;

internal static class MediaExtensions
{
    /// <summary>Kept as a forwarder so the endpoints read the same as before; the table itself
    /// moved to <see cref="MediaFileExtensions"/>, which the media worker also reads.</summary>
    public static string ToExtension(string contentType) => MediaFileExtensions.ToExtension(contentType);

    public static string ProcessSubject(MediaCategory category) => category switch
    {
        MediaCategory.Image => Dcms.Shared.Contracts.Messaging.Subjects.MediaProcessImage,
        MediaCategory.Video => Dcms.Shared.Contracts.Messaging.Subjects.MediaProcessVideo,
        MediaCategory.Audio => Dcms.Shared.Contracts.Messaging.Subjects.MediaProcessAudio,
        _ => string.Empty,
    };
}
