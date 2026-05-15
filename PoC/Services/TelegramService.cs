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
    private readonly DownloadManifest _manifest;
    private readonly ILogger<TelegramService> _logger;

    public TelegramService(
        Client client,
        IOptions<TelegramOptions> options,
        DownloadManifest manifest,
        ILogger<TelegramService> logger)
    {
        _client = client;
        _options = options.Value;
        _manifest = manifest;
        _logger = logger;
    }

    // ── Lifecycle ─────────────────────────────────────────────────────────────
    public async Task ConnectAsync(CancellationToken ct)
    {
        _logger.LogInformation("Connecting to Telegram…");
        await _client.LoginUserIfNeeded().WaitAsync(ct);
        _logger.LogInformation("Logged in as: {User}", _client.User);
    }

    // ── Peer / chat resolution ────────────────────────────────────────────────
    public async Task<(InputPeer Peer, long ChatId)> ResolvePeerAsync(string input, CancellationToken ct)
    {
        if (long.TryParse(input, out var numId))
        {
            var contacts = await _client.Messages_GetAllDialogs().WaitAsync(ct);
            if (contacts.chats.TryGetValue(numId, out var chat))
                return (chat.ToInputPeer(), numId);
            if (contacts.users.TryGetValue(numId, out var user))
                return (user.ToInputPeer(), numId);
            throw new InvalidOperationException($"ID {numId} not found in dialogs.");
        }

        var username = input.TrimStart('@');
        var resolved = await _client.Contacts_ResolveUsername(username).WaitAsync(ct);
        return resolved.peer switch
        {
            PeerChannel c => (resolved.chats[c.channel_id].ToInputPeer(), c.channel_id),
            PeerChat g => (resolved.chats[g.chat_id].ToInputPeer(), g.chat_id),
            PeerUser u => (resolved.users[u.user_id].ToInputPeer(), u.user_id),
            _ => throw new InvalidOperationException("Unknown peer type.")
        };
    }

    public async Task ListChatsAsync(CancellationToken ct)
    {
        Console.WriteLine("\nLoading dialogs…");
        var dialogs = await _client.Messages_GetAllDialogs().WaitAsync(ct);

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

    // ── Media discovery ───────────────────────────────────────────────────────
    public async Task<IReadOnlyList<MediaItem>> GetMediaAsync(
        InputPeer peer, long chatId, int limit, IReadOnlySet<MediaKind> kinds, CancellationToken ct)
    {
        var messages = await _client.Messages_GetHistory(peer, limit: limit).WaitAsync(ct);
        return ExtractMedia(messages.Messages, chatId, kinds);
    }

    public async Task<IReadOnlyList<MediaItem>> SearchMediaAsync(
        InputPeer peer, long chatId, string query, IReadOnlySet<MediaKind> kinds, int limit, CancellationToken ct)
    {
        var result = await _client.Messages_Search(peer, query, limit: limit).WaitAsync(ct);
        return ExtractMedia(result.Messages, chatId, kinds);
    }

    // ── Stories ───────────────────────────────────────────────────────────────
    public async Task<IReadOnlyList<MediaItem>> GetActiveStoriesAsync(
        InputPeer peer, long peerId, IReadOnlySet<MediaKind> kinds, CancellationToken ct)
    {
        var result = await _client.Stories_GetPeerStories(peer).WaitAsync(ct);
        var raw = result.stories?.stories;
        _logger.LogInformation("Stories_GetPeerStories returned {Count} story item(s)", raw?.Length ?? 0);
        return ExtractStories(raw, peerId, kinds);
    }

    public async Task<IReadOnlyList<MediaItem>> GetPinnedStoriesAsync(
        InputPeer peer, long peerId, int limit, IReadOnlySet<MediaKind> kinds, CancellationToken ct)
    {
        var result = await _client.Stories_GetPinnedStories(peer, limit: limit).WaitAsync(ct);
        _logger.LogInformation("Stories_GetPinnedStories returned {Count} story item(s)", result.stories?.Length ?? 0);
        return ExtractStories(result.stories, peerId, kinds);
    }

    private static IReadOnlyList<MediaItem> ExtractStories(
        StoryItemBase[]? stories, long peerId, IReadOnlySet<MediaKind> kinds)
    {
        var items = new List<MediaItem>();
        if (stories is null) return items;

        foreach (var s in stories)
        {
            if (s is not StoryItem story) continue;
            var item = MediaItem.TryFromStory(peerId, story);
            if (item is null) continue;
            if (kinds.Count > 0 && !kinds.Contains(item.Kind)) continue;
            items.Add(item);
        }
        return items;
    }

    private static IReadOnlyList<MediaItem> ExtractMedia(
        IEnumerable<MessageBase> messages, long chatId, IReadOnlySet<MediaKind> kinds)
    {
        var items = new List<MediaItem>();
        foreach (var msg in messages)
        {
            if (msg is not Message m) continue;
            var item = MediaItem.TryFrom(chatId, m);
            if (item is null) continue;
            if (kinds.Count > 0 && !kinds.Contains(item.Kind)) continue;
            items.Add(item);
        }
        return items;
    }

    // ── Download ──────────────────────────────────────────────────────────────
    public async Task<int> DownloadManyAsync(
        IReadOnlyList<MediaItem> items,
        Action<int, int> onCompleted,
        CancellationToken ct)
    {
        int parallel = Math.Max(1, _options.MaxConcurrentDownloads);
        int done = 0;
        int skipped = 0;

        var opts = new ParallelOptions
        {
            MaxDegreeOfParallelism = parallel,
            CancellationToken = ct
        };

        await Parallel.ForEachAsync(items, opts, async (item, token) =>
        {
            if (_manifest.Contains(item.ChatId, item.MsgId, item.IsStory))
            {
                Interlocked.Increment(ref skipped);
                int d = Interlocked.Increment(ref done);
                onCompleted(d, items.Count);
                _logger.LogInformation("Skipping already-downloaded {MsgId} ({Name})", item.MsgId, item.DisplayName);
                return;
            }

            try
            {
                bool silent = parallel > 1;
                await DownloadMediaAsync(item, silent, token);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to download msg {MsgId} ({Name})", item.MsgId, item.DisplayName);
            }

            int d2 = Interlocked.Increment(ref done);
            onCompleted(d2, items.Count);
        });

        return skipped;
    }

    public async Task DownloadMediaAsync(MediaItem item, bool silentProgress, CancellationToken ct)
    {
        var outputDir = _options.ResolvedOutputDirectory;
        Directory.CreateDirectory(outputDir);

        var fileName = FileHelpers.SanitizeFileName(item.DisplayName);
        var outPath = Path.Combine(outputDir, fileName);

        if (File.Exists(outPath))
        {
            var stem = Path.GetFileNameWithoutExtension(outPath);
            var ext = Path.GetExtension(outPath);
            outPath = Path.Combine(outputDir, $"{stem}_{item.MsgId}{ext}");
        }

        _logger.LogInformation("Downloading {Kind} {Name} ({Size}) → {OutPath}",
            item.Kind, fileName, FileHelpers.FormatSize(item.Size), outPath);

        var sw = System.Diagnostics.Stopwatch.StartNew();

        await using (var fs = new FileStream(outPath, FileMode.Create, FileAccess.Write,
                                              FileShare.None, FileBufferSize, useAsync: true))
        {
            Client.ProgressCallback? cb = silentProgress
                ? null
                : (transferred, total) => ConsoleUi.PrintProgress(transferred, total, sw.Elapsed);

            if (item.Document is not null)
            {
                await _client.DownloadFileAsync(item.Document, fs, progress: cb).WaitAsync(ct);
            }
            else if (item.Photo is not null)
            {
                await _client.DownloadFileAsync(item.Photo, fs, progress: cb).WaitAsync(ct);
            }
            else
            {
                throw new InvalidOperationException("MediaItem has neither Document nor Photo.");
            }
        }

        if (!silentProgress) Console.WriteLine();
        _manifest.Record(item.ChatId, item.MsgId, outPath, item.IsStory);
        _logger.LogInformation("Saved {OutPath} in {Elapsed:mm\\:ss}", outPath, sw.Elapsed);
    }
}
