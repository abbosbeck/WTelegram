using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TelegramDownloader.Configuration;
using TelegramDownloader.Helpers;
using TelegramDownloader.Ui;
using TL;
using WTelegram;

namespace TelegramDownloader.Services;

internal sealed class TelegramService
{
    private const int FileBufferSize = 1 << 20;

    private readonly Client _client;
    private readonly TelegramOptions _options;
    private readonly ILogger<TelegramService> _logger;

    public TelegramService(Client client, IOptions<TelegramOptions> options, ILogger<TelegramService> logger)
    {
        _client = client;
        _options = options.Value;
        _logger = logger;
    }

    public async Task ConnectAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("Connecting to Telegram…");
        await _client.LoginUserIfNeeded().WaitAsync(cancellationToken);
        _logger.LogInformation("Logged in as: {User}", _client.User);
    }

    public async Task<InputPeer> ResolvePeerAsync(string input, CancellationToken cancellationToken)
    {
        if (long.TryParse(input, out var numId))
        {
            var contacts = await _client.Messages_GetAllDialogs().WaitAsync(cancellationToken);
            if (contacts.chats.TryGetValue(numId, out var chat))
                return chat.ToInputPeer();
            if (contacts.users.TryGetValue(numId, out var user))
                return user.ToInputPeer();
            throw new InvalidOperationException($"ID {numId} not found in dialogs.");
        }

        var username = input.TrimStart('@');
        var resolved = await _client.Contacts_ResolveUsername(username).WaitAsync(cancellationToken);
        return resolved.peer switch
        {
            PeerChannel c => resolved.chats[c.channel_id].ToInputPeer(),
            PeerChat g => resolved.chats[g.chat_id].ToInputPeer(),
            PeerUser u => resolved.users[u.user_id].ToInputPeer(),
            _ => throw new InvalidOperationException("Unknown peer type.")
        };
    }

    public async Task<IReadOnlyList<VideoItem>> GetVideosAsync(InputPeer peer, int limit, CancellationToken cancellationToken)
    {
        var messages = await _client.Messages_GetHistory(peer, limit: limit).WaitAsync(cancellationToken);

        var videos = new List<VideoItem>();
        foreach (var msg in messages.Messages)
        {
            if (msg is Message { media: MessageMediaDocument { document: Document doc } }
                && doc.mime_type.StartsWith("video/"))
            {
                videos.Add(new VideoItem(msg.ID, doc));
            }
        }

        return videos;
    }

    public async Task ListChatsAsync(CancellationToken cancellationToken)
    {
        Console.WriteLine("\nLoading dialogs…");
        var dialogs = await _client.Messages_GetAllDialogs().WaitAsync(cancellationToken);

        Console.WriteLine($"\n{"ID",-15} {"Type",-10} {"Title"}");
        Console.WriteLine(new string('─', 60));

        foreach (var (id, chat) in dialogs.chats)
        {
            var type = chat switch
            {
                Channel { IsChannel: true } => "Channel",
                Channel => "Group",
                Chat => "Group",
                _ => "?"
            };
            Console.WriteLine($"{id,-15} {type,-10} {chat.Title}");
        }

        foreach (var (id, user) in dialogs.users)
        {
            if (!user.IsActive) continue;
            Console.WriteLine($"{id,-15} {"User",-10} {user.first_name} {user.last_name}");
        }

        Console.WriteLine();
    }

    public async Task DownloadDocumentAsync(Document doc, int msgId, CancellationToken cancellationToken)
    {
        var outputDir = _options.ResolvedOutputDirectory;
        var attr = doc.attributes.OfType<DocumentAttributeFilename>().FirstOrDefault();
        var fileName = FileHelpers.SanitizeFileName(attr?.file_name ?? $"{doc.id}.mp4");
        var outPath = Path.Combine(outputDir, fileName);

        if (File.Exists(outPath))
        {
            var stem = Path.GetFileNameWithoutExtension(outPath);
            var ext = Path.GetExtension(outPath);
            outPath = Path.Combine(outputDir, $"{stem}_{msgId}{ext}");
        }

        _logger.LogInformation("Downloading {FileName} ({Size}) to {OutPath}",
            fileName, FileHelpers.FormatSize(doc.size), outPath);

        var sw = System.Diagnostics.Stopwatch.StartNew();

        await using var fs = new FileStream(outPath, FileMode.Create, FileAccess.Write,
                                             FileShare.None, FileBufferSize, useAsync: true);

        await _client.DownloadFileAsync(doc, fs,
            progress: (transferred, total) => ConsoleUi.PrintProgress(transferred, total, sw.Elapsed))
            .WaitAsync(cancellationToken);

        Console.WriteLine();
        _logger.LogInformation("Saved {OutPath} ({Size} in {Elapsed:mm\\:ss})",
            outPath, FileHelpers.FormatSize(doc.size), sw.Elapsed);
    }
}
