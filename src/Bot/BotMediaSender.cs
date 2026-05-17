using Domain.Common;
using Domain.Downloads;
using Infrastructure.Downloads;
using Microsoft.Extensions.Logging;
using Telegram.Bot;
using Telegram.Bot.Types;
using TgInputFile = Telegram.Bot.Types.InputFile;

namespace Bot;

/// <summary>
/// Sends media to a chat. Always tries the cheapest path first (cached file_id),
/// then falls back to streaming the file from disk. After every successful upload
/// the returned file_id is recorded so the next request becomes a single API call.
/// </summary>
internal sealed class BotMediaSender
{
    private readonly ITelegramBotClient _bot;
    private readonly DownloadManifest _manifest;
    private readonly ILogger<BotMediaSender> _logger;

    public BotMediaSender(
        ITelegramBotClient bot,
        DownloadManifest manifest,
        ILogger<BotMediaSender> logger)
    {
        _bot = bot;
        _manifest = manifest;
        _logger = logger;
    }

    /// <summary>
    /// If a file_id is cached for <paramref name="urlKey"/>, ships it as a string
    /// (no upload) and returns true. Otherwise returns false.
    /// </summary>
    public async Task<bool> TrySendCachedUrlAsync(long chatId, string urlKey, bool audioOnly, CancellationToken ct)
    {
        var cachedId = _manifest.GetUrlFileId(urlKey);
        if (cachedId is null) return false;
        _logger.LogInformation("Reusing cached file_id for {Url}", urlKey);
        await SendFromFileIdAsync(chatId, cachedId, audioOnly, ct);
        return true;
    }

    /// <summary>
    /// Uploads the local file once, caches the returned file_id, deletes the file.
    /// </summary>
    public async Task UploadUrlAsync(long chatId, string urlKey, string localPath, bool audioOnly, CancellationToken ct)
    {
        await using var fs = File.OpenRead(localPath);
        var name = Path.GetFileName(localPath);
        var inputFile = TgInputFile.FromStream(fs, name);

        var sent = audioOnly
            ? await _bot.SendAudio(chatId, inputFile, cancellationToken: ct)
            : await _bot.SendVideo(chatId, inputFile, supportsStreaming: true, cancellationToken: ct);

        var fileId = ExtractFileId(sent);
        if (fileId is not null)
            _manifest.RecordUrlFileId(urlKey, fileId);

        TryDeleteLocal(localPath);
    }

    /// <summary>
    /// If a file_id is cached for this (ChatId, MsgId, IsStory), ships it and returns true.
    /// </summary>
    public async Task<bool> TrySendCachedMediaAsync(long chatId, MediaItem item, CancellationToken ct)
    {
        var cachedId = _manifest.GetMessageFileId(item.ChatId, item.MsgId, item.IsStory);
        if (cachedId is null) return false;
        _logger.LogInformation("Reusing cached file_id for {ChatId}:{MsgId}", item.ChatId, item.MsgId);
        await SendFromFileIdAsync(chatId, cachedId, item.Kind == MediaKind.Audio, ct);
        return true;
    }

    /// <summary>
    /// Uploads <paramref name="localPath"/> once and caches the file_id keyed by
    /// (item.ChatId, item.MsgId, item.IsStory).
    /// </summary>
    public async Task UploadMediaAsync(long chatId, MediaItem item, string localPath, CancellationToken ct)
    {
        await using var fs = File.OpenRead(localPath);
        var name = Path.GetFileName(localPath);
        var inputFile = TgInputFile.FromStream(fs, name);

        Message sent = item.Kind switch
        {
            MediaKind.Video    => await _bot.SendVideo(chatId, inputFile, supportsStreaming: true, cancellationToken: ct),
            MediaKind.Audio    => await _bot.SendAudio(chatId, inputFile, cancellationToken: ct),
            MediaKind.Photo    => await _bot.SendPhoto(chatId, inputFile, cancellationToken: ct),
            _                  => await _bot.SendDocument(chatId, inputFile, cancellationToken: ct),
        };

        var fileId = ExtractFileId(sent);
        if (fileId is not null)
            _manifest.RecordMessageFileId(item.ChatId, item.MsgId, item.IsStory, fileId);

        TryDeleteLocal(localPath);
    }

    private async Task SendFromFileIdAsync(long chatId, string fileId, bool audioOnly, CancellationToken ct)
    {
        var input = TgInputFile.FromFileId(fileId);
        if (audioOnly)
            await _bot.SendAudio(chatId, input, cancellationToken: ct);
        else
            await _bot.SendVideo(chatId, input, supportsStreaming: true, cancellationToken: ct);
    }

    private static string? ExtractFileId(Message m) =>
        m.Video?.FileId
        ?? m.Audio?.FileId
        ?? m.Document?.FileId
        ?? m.Photo?.LastOrDefault()?.FileId
        ?? m.Voice?.FileId
        ?? m.VideoNote?.FileId;

    private void TryDeleteLocal(string path)
    {
        try { File.Delete(path); }
        catch (Exception ex) { _logger.LogDebug(ex, "Failed to delete {Path}", path); }
    }
}
