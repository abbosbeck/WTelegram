using TL;

namespace Domain.Downloads;

public enum MediaKind
{
    Video,
    Photo,
    Audio,
    Document
}

/// <summary>
/// A unified wrapper over a downloadable Telegram media object (from a message or a story).
/// Exactly one of <see cref="Document"/> or <see cref="Photo"/> is non-null.
/// </summary>
public sealed record MediaItem(
    long ChatId,
    int MsgId,
    MediaKind Kind,
    string DisplayName,
    long Size,
    TimeSpan? Duration,
    Document? Document,
    Photo? Photo,
    bool IsStory = false)
{
    public static MediaItem? TryFrom(long chatId, Message msg) =>
        FromMedia(chatId, msg.ID, msg.media, isStory: false);

    public static MediaItem? TryFromStory(long peerId, StoryItem story) =>
        FromMedia(peerId, story.id, story.media, isStory: true);

    private static MediaItem? FromMedia(long chatId, int id, MessageMedia? media, bool isStory)
    {
        switch (media)
        {
            case MessageMediaDocument { document: Document doc }:
            {
                var kind = ClassifyDocument(doc);
                var name = doc.attributes.OfType<DocumentAttributeFilename>().FirstOrDefault()?.file_name
                           ?? DefaultDocumentName(doc, kind, isStory, id);
                var video = doc.attributes.OfType<DocumentAttributeVideo>().FirstOrDefault();
                var audio = doc.attributes.OfType<DocumentAttributeAudio>().FirstOrDefault();
                TimeSpan? duration = video is not null
                    ? TimeSpan.FromSeconds(video.duration)
                    : audio is not null ? TimeSpan.FromSeconds(audio.duration) : null;

                return new MediaItem(chatId, id, kind, name, doc.size, duration, doc, null, isStory);
            }

            case MessageMediaPhoto { photo: Photo photo }:
            {
                var largest = photo.LargestPhotoSize;
                long size = largest?.FileSize ?? 0;
                var name = isStory ? $"story_{id}_{photo.id}.jpg" : $"photo_{photo.id}.jpg";
                return new MediaItem(chatId, id, MediaKind.Photo, name, size, null, null, photo, isStory);
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

    private static string DefaultDocumentName(Document doc, MediaKind kind, bool isStory, int id)
    {
        var prefix = isStory ? $"story_{id}_" : "";
        return kind switch
        {
            MediaKind.Video => $"{prefix}video_{doc.id}.mp4",
            MediaKind.Audio => $"{prefix}audio_{doc.id}.mp3",
            MediaKind.Photo => $"{prefix}image_{doc.id}.jpg",
            _ => $"{prefix}file_{doc.id}.bin"
        };
    }
}
