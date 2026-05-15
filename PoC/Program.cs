using System.Text.RegularExpressions;
using TL;
using WTelegram;

namespace TelegramDownloader;

class Program
{
    // ── Config ────────────────────────────────────────────────────────────────
    // Get these from https://my.telegram.org → API development tools
    private const int ApiId = 39142364;          // <-- replace
    private const string ApiHash = "1b28c9bbde7850eded0b523eaab82f48"; // <-- replace

    // Where to save downloaded files
    private static readonly string OutputDir =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                     "TelegramDownloads");
    // ─────────────────────────────────────────────────────────────────────────

    static async Task Main(string[] args)
    {
        Directory.CreateDirectory(OutputDir);

        // WTelegramClient stores session in a local file so you only log in once
        using var client = new Client(Config);

        Console.WriteLine("Connecting to Telegram…");
        await client.LoginUserIfNeeded();
        Console.WriteLine($"Logged in as: {client.User}\n");

        while (true)
        {
            Console.WriteLine("════════════════════════════════════");
            Console.WriteLine("1 – Download video from a chat/channel");
            Console.WriteLine("2 – List recent chats");
            Console.WriteLine("0 – Exit");
            Console.Write("Choice: ");
            var choice = Console.ReadLine()?.Trim();

            switch (choice)
            {
                case "1": await DownloadVideoFlow(client); break;
                case "2": await ListChats(client); break;
                case "0": return;
                default: Console.WriteLine("Unknown option.\n"); break;
            }
        }
    }

    // ── Interactive download flow ─────────────────────────────────────────────
    static async Task DownloadVideoFlow(Client client)
    {
        Console.Write("\nEnter chat username or ID (e.g. @mychannel or 123456789): ");
        var input = Console.ReadLine()?.Trim() ?? "";

        InputPeer peer;
        try
        {
            peer = await ResolvePeer(client, input);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[ERROR] Could not resolve chat: {ex.Message}\n");
            return;
        }

        Console.Write("How many recent messages to scan? [default 50]: ");
        var limitStr = Console.ReadLine()?.Trim();
        int limit = int.TryParse(limitStr, out var n) ? n : 50;

        Console.WriteLine($"\nFetching last {limit} messages…");
        var messages = await client.Messages_GetHistory(peer, limit: limit);

        // Collect all video documents
        var videos = new List<(int MsgId, TL.Document Doc)>();
        foreach (var msg in messages.Messages)
        {
            if (msg is Message { media: MessageMediaDocument { document: TL.Document doc } }
                && doc.mime_type.StartsWith("video/"))
            {
                videos.Add((msg.ID, doc));
            }
        }

        if (videos.Count == 0)
        {
            Console.WriteLine("No video messages found in that range.\n");
            return;
        }

        Console.WriteLine($"\nFound {videos.Count} video(s):\n");
        for (int i = 0; i < videos.Count; i++)
        {
            var (msgId, doc) = videos[i];
            var attr = doc.attributes.OfType<DocumentAttributeFilename>().FirstOrDefault();
            var video = doc.attributes.OfType<DocumentAttributeVideo>().FirstOrDefault();
            var name = attr?.file_name ?? $"video_{doc.id}";
            var size = FormatSize(doc.size);
            var dur = video is not null ? TimeSpan.FromSeconds(video.duration).ToString(@"mm\:ss") : "?";
            Console.WriteLine($"  [{i + 1}] MsgID={msgId}  {name}  {size}  {dur}");
        }

        Console.Write("\nWhich video(s) to download? (e.g. 1,3 or 1-5 or 'all'): ");
        var sel = Console.ReadLine()?.Trim() ?? "";
        var indices = ParseSelection(sel, videos.Count);

        foreach (var idx in indices)
        {
            var (msgId, doc) = videos[idx];
            await DownloadDocument(client, doc, peer, msgId);
        }
    }

    // ── Core download logic ───────────────────────────────────────────────────
    static async Task DownloadDocument(Client client, Document doc,
                                       InputPeer peer, int msgId)
    {
        var attr = doc.attributes.OfType<DocumentAttributeFilename>().FirstOrDefault();
        var fileName = SanitizeFileName(attr?.file_name ?? $"{doc.id}.mp4");
        var outPath = Path.Combine(OutputDir, fileName);

        // Avoid overwriting
        if (File.Exists(outPath))
        {
            var stem = Path.GetFileNameWithoutExtension(outPath);
            var ext = Path.GetExtension(outPath);
            outPath = Path.Combine(OutputDir, $"{stem}_{msgId}{ext}");
        }

        Console.WriteLine($"\n↓ Downloading: {fileName}");
        Console.WriteLine($"  Size : {FormatSize(doc.size)}");
        Console.WriteLine($"  To   : {outPath}");

        long downloaded = 0;
        var sw = System.Diagnostics.Stopwatch.StartNew();

        await using var fs = new FileStream(outPath, FileMode.Create, FileAccess.Write,
                                             FileShare.None, 1 << 20, useAsync: true);

        // WTelegramClient handles MTProto chunking + decryption internally
        await client.DownloadFileAsync(doc, fs,
            progress: (transferred, total) =>
            {
                downloaded = transferred;
                PrintProgress(transferred, total, sw.Elapsed);
            });

        Console.WriteLine();
        Console.WriteLine($"  ✓ Saved to {outPath}  ({FormatSize(downloaded)} in {sw.Elapsed:mm\\:ss})\n");
    }

    // ── List chats ────────────────────────────────────────────────────────────
    static async Task ListChats(Client client)
    {
        Console.WriteLine("\nLoading dialogs…");
        var dialogs = await client.Messages_GetAllDialogs();

        Console.WriteLine($"\n{"ID",-15} {"Type",-10} {"Title"}");
        Console.WriteLine(new string('─', 60));

        foreach (var (id, chat) in dialogs.chats)
        {
            var type = chat switch
            {
                TL.Channel {IsChannel: true } => "Channel",
                TL.Channel => "Group",
                TL.Chat => "Group",
                _ => "?"
            };
            Console.WriteLine($"{id,-15} {type,-10} {chat.Title}");
        }

        foreach (var (id, user) in dialogs.users)
        {
            if (user.IsActive) continue;
            Console.WriteLine($"{id,-15} {"User",-10} {user.first_name} {user.last_name}");
        }

        Console.WriteLine();
    }

    // ── Helpers ───────────────────────────────────────────────────────────────
    static async Task<InputPeer> ResolvePeer(Client client, string input)
    {
        // Numeric ID
        if (long.TryParse(input, out var numId))
        {
            var contacts = await client.Messages_GetAllDialogs();
            if (contacts.chats.TryGetValue(numId, out var chat))
                return chat.ToInputPeer();
            if (contacts.users.TryGetValue(numId, out var user))
                return user.ToInputPeer();
            throw new Exception($"ID {numId} not found in dialogs.");
        }

        // @username
        var username = input.TrimStart('@');
        var resolved = await client.Contacts_ResolveUsername(username);
        return resolved.peer switch
        {
            PeerChannel c => resolved.chats[c.channel_id].ToInputPeer(),
            PeerChat g => resolved.chats[g.chat_id].ToInputPeer(),
            PeerUser u => resolved.users[u.user_id].ToInputPeer(),
            _ => throw new Exception("Unknown peer type.")
        };
    }

    static IEnumerable<int> ParseSelection(string sel, int max)
    {
        if (sel.Equals("all", StringComparison.OrdinalIgnoreCase))
            return Enumerable.Range(0, max);

        var indices = new HashSet<int>();
        foreach (var part in sel.Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            var p = part.Trim();
            if (Regex.IsMatch(p, @"^\d+-\d+$"))
            {
                var bounds = p.Split('-');
                int lo = int.Parse(bounds[0]) - 1;
                int hi = int.Parse(bounds[1]) - 1;
                for (int i = lo; i <= Math.Min(hi, max - 1); i++) indices.Add(i);
            }
            else if (int.TryParse(p, out var n))
            {
                int i = n - 1;
                if (i >= 0 && i < max) indices.Add(i);
            }
        }
        return indices.OrderBy(x => x);
    }

    static void PrintProgress(long transferred, long total, TimeSpan elapsed)
    {
        double pct = total > 0 ? (double)transferred / total * 100 : 0;
        double mbps = elapsed.TotalSeconds > 0
                       ? transferred / 1_048_576.0 / elapsed.TotalSeconds
                       : 0;
        int bars = (int)(pct / 2);
        string bar = $"[{new string('█', bars)}{new string('░', 50 - bars)}]";
        Console.Write($"\r  {bar} {pct,5:F1}%  {FormatSize(transferred)}/{FormatSize(total)}  {mbps:F2} MB/s  ");
    }

    static string FormatSize(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} B",
        < 1_048_576 => $"{bytes / 1024.0:F1} KB",
        < 1_073_741_824 => $"{bytes / 1_048_576.0:F1} MB",
        _ => $"{bytes / 1_073_741_824.0:F2} GB"
    };

    static string SanitizeFileName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        return string.Concat(name.Select(c => invalid.Contains(c) ? '_' : c));
    }

    // ── WTelegramClient config callback ───────────────────────────────────────
    static string? Config(string what)
    {
        return what switch
        {
            "api_id" => ApiId.ToString(),
            "api_hash" => ApiHash,
            "phone_number" => Prompt("Phone number (international format, e.g. +998901234567): "),
            "verification_code" => Prompt("Verification code from Telegram: "),
            "password" => Prompt("2FA password (if enabled): "),
            "session_pathname" => "telegram_session.dat", // persisted locally
            _ => null
        };
    }

    static string Prompt(string message)
    {
        Console.Write(message);
        return Console.ReadLine()?.Trim() ?? "";
    }
}
