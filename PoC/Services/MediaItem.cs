using TL;

namespace TelegramDownloader.Services;

internal enum MediaKind
{
    Video,
    Photo,
    Audio,
    Document
}

/// <summary>
/// A unified wrapper over a downloadable Telegram message media object.
/// Exactly one of <see cref="Document"/> or <see cref="Photo"/> is non-null.
/// </summary>
internal sealed record MediaItem(
    long ChatId,
    int MsgId,
    MediaKind Kind,
    string DisplayName,
    long Size,
    TimeSpan? Duration,
    Document? Document,
    Photo? Photo)
{
    public static MediaItem? TryFrom(long chatId, Message msg)
    {
        switch (msg.media)
        {
            case MessageMediaDocument { document: Document doc }:
            {
                var kind = ClassifyDocument(doc);
                var name = doc.attributes.OfType<DocumentAttributeFilename>().FirstOrDefault()?.file_name
                           ?? DefaultDocumentName(doc, kind);
                var video = doc.attributes.OfType<DocumentAttributeVideo>().FirstOrDefault();
                var audio = doc.attributes.OfType<DocumentAttributeAudio>().FirstOrDefault();
                TimeSpan? duration = video is not null
                    ? TimeSpan.FromSeconds(video.duration)
                    : audio is not null ? TimeSpan.FromSeconds(audio.duration) : null;

                return new MediaItem(chatId, msg.ID, kind, name, doc.size, duration, doc, null);
            }

            case MessageMediaPhoto { photo: Photo photo }:
            {
                var largest = photo.LargestPhotoSize;
                long size = largest?.FileSize ?? 0;
                var name = $"photo_{photo.id}.jpg";
                return new MediaItem(chatId, msg.ID, MediaKind.Photo, name, size, null, null, photo);
            }

            default:
                return null;
        }
    }

    private static MediaKind ClassifyDocument(Document doc)
    {
        var mime = doc.mime_type ?? "";
        if (mime.StartsWith("video/")) return MediaKind.Video;
        if (mime.StartsWith("audio/")) return MediaKind.Audio;
        if (mime.StartsWith("image/")) return MediaKind.Photo;
        return MediaKind.Document;
    }

    private static string DefaultDocumentName(Document doc, MediaKind kind) => kind switch
    {
        MediaKind.Video => $"video_{doc.id}.mp4",
        MediaKind.Audio => $"audio_{doc.id}.mp3",
        MediaKind.Photo => $"image_{doc.id}.jpg",
        _ => $"file_{doc.id}.bin"
    };
}
