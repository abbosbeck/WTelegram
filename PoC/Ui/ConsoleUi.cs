using System.Text.RegularExpressions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TelegramDownloader.Configuration;
using TelegramDownloader.Helpers;
using TelegramDownloader.Services;
using TL;
using WTelegram;

namespace TelegramDownloader.Ui;

internal sealed class ConsoleUi : BackgroundService
{
    private const int ProgressBarWidth = 50;
    private const int DefaultMessageScanLimit = 50;

    private static readonly IReadOnlySet<MediaKind> AllKinds = new HashSet<MediaKind>();

    private readonly TelegramService _telegram;
    private readonly MessageLinkResolver _linkResolver;
    private readonly WebVideoDownloader _webDownloader;
    private readonly SessionPool _sessionPool;
    private readonly LoginCoordinator _loginCoordinator;
    private readonly TelegramOptions _telegramOptions;
    private readonly IConsolePrompt _prompt;
    private readonly IHostApplicationLifetime _lifetime;
    private readonly ILogger<ConsoleUi> _logger;

    public ConsoleUi(
        TelegramService telegram,
        MessageLinkResolver linkResolver,
        WebVideoDownloader webDownloader,
        SessionPool sessionPool,
        LoginCoordinator loginCoordinator,
        IOptions<TelegramOptions> telegramOptions,
        IConsolePrompt prompt,
        IHostApplicationLifetime lifetime,
        ILogger<ConsoleUi> logger)
    {
        _telegram = telegram;
        _linkResolver = linkResolver;
        _webDownloader = webDownloader;
        _sessionPool = sessionPool;
        _loginCoordinator = loginCoordinator;
        _telegramOptions = telegramOptions.Value;
        _prompt = prompt;
        _lifetime = lifetime;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await RunMenuAsync(stoppingToken);
        }
        catch (OperationCanceledException) { /* graceful shutdown */ }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Fatal error in console UI");
        }
        finally
        {
            _lifetime.StopApplication();
        }
    }

    private async Task RunMenuAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            Console.WriteLine("════════════════════════════════════");
            Console.WriteLine("1 – Download media from a chat/channel");
            Console.WriteLine("2 – Search inside a chat & download");
            Console.WriteLine("3 – Download by message link (t.me/…)");
            Console.WriteLine("4 – List recent chats");
            Console.WriteLine("5 – Download active stories from a user/channel");
            Console.WriteLine("6 – Download pinned stories from a user/channel");
            Console.WriteLine("7 – Download video from URL (YouTube, Instagram, TikTok, …)");
            Console.WriteLine("0 – Exit");
            Console.Write("Choice: ");
            var choice = _prompt.ReadLineTrimmed();

            try
            {
                switch (choice)
                {
                    case "1": await DownloadFromChatFlowAsync(ct); break;
                    case "2": await SearchAndDownloadFlowAsync(ct); break;
                    case "3": await DownloadByLinkFlowAsync(ct); break;
                    case "4": await ListChatsFlowAsync(ct); break;
                    case "5": await DownloadActiveStoriesFlowAsync(ct); break;
                    case "6": await DownloadPinnedStoriesFlowAsync(ct); break;
                    case "7": await DownloadFromUrlFlowAsync(ct); break;
                    case "0": return;
                    default: Console.WriteLine("Unknown option.\n"); break;
                }
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Operation failed");
            }
        }
    }

    // ── Owner Client acquisition (drives login state machine via console) ─────
    private async Task<Client> GetOwnerClientAsync(CancellationToken ct)
    {
        if (_telegramOptions.OwnerUserId == 0)
            throw new InvalidOperationException(
                "Telegram:OwnerUserId is not configured. " +
                "Set it to your numeric Telegram user ID (ask @userinfobot if you don't know it).");

        var userId = _telegramOptions.OwnerUserId;
        if (_sessionPool.IsCached(userId))
            return await _sessionPool.AcquireAsync(userId, ct);

        // Register a LoginSession so the SessionPool's config callback can ask
        // the console for phone / code / 2FA password as WTelegramClient requests them.
        var login = new LoginSession(userId, null!);
        if (!_loginCoordinator.TryRegister(userId, login))
            login = _loginCoordinator.Get(userId)!;

        // Drive prompts in a side task so they're presented as soon as WTelegram asks.
        _ = Task.Run(() => DrivePromptsAsync(login));

        try
        {
            return await _sessionPool.AcquireAsync(userId, ct);
        }
        catch
        {
            _loginCoordinator.Cancel(userId);
            throw;
        }
    }

    private async Task DrivePromptsAsync(LoginSession login)
    {
        // WTelegramClient asks for these one by one. Each TCS only resolves once.
        try
        {
            if (login.IsPhoneAwaited)
            {
                Console.WriteLine();
                var phone = _prompt.Ask("Phone number (international format, e.g. +998901234567): ");
                login.SubmitPhone(phone);
            }
            // Code prompt – wait a tick so the phone is processed and WTelegram requests the code next.
            while (login.IsPhoneAwaited) await Task.Delay(100);
            if (login.IsCodeAwaited)
            {
                Console.WriteLine();
                Console.WriteLine("Telegram sent a verification code. " +
                    "On a fresh device you'll find it in: Telegram app → Settings → Devices → the new session entry.");
                var code = _prompt.Ask("Verification code: ");
                login.SubmitCode(code);
            }
            while (login.IsCodeAwaited) await Task.Delay(100);
            if (login.IsPasswordAwaited)
            {
                Console.WriteLine();
                var pwd = _prompt.Ask("2FA password (leave blank if none): ");
                login.SubmitPassword(pwd);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Login prompt driver failed");
        }
    }

    // ── Flow 1: scan history ──────────────────────────────────────────────────
    private async Task DownloadFromChatFlowAsync(CancellationToken ct)
    {
        var input = _prompt.Ask("\nEnter chat username or ID (e.g. @mychannel or 123456789): ");
        var client = await GetOwnerClientAsync(ct);
        var (peer, chatId) = await _telegram.ResolvePeerAsync(client, input, ct);

        var limitStr = _prompt.Ask($"How many recent messages to scan? [default {DefaultMessageScanLimit}]: ");
        int limit = int.TryParse(limitStr, out var n) ? n : DefaultMessageScanLimit;

        var kinds = AskMediaKinds();

        Console.WriteLine($"\nFetching last {limit} messages…");
        var items = await _telegram.GetMediaAsync(client, peer, chatId, limit, kinds, ct);

        await PromptAndDownloadAsync(client, items, ct);
    }

    // ── Flow 2: in-chat search ────────────────────────────────────────────────
    private async Task SearchAndDownloadFlowAsync(CancellationToken ct)
    {
        var input = _prompt.Ask("\nEnter chat username or ID: ");
        var client = await GetOwnerClientAsync(ct);
        var (peer, chatId) = await _telegram.ResolvePeerAsync(client, input, ct);

        var query = _prompt.Ask("Search query (text to match in messages): ");
        var kinds = AskMediaKinds();
        var limitStr = _prompt.Ask("Max results [default 100]: ");
        int limit = int.TryParse(limitStr, out var n) ? n : 100;

        Console.WriteLine($"\nSearching for \"{query}\"…");
        var items = await _telegram.SearchMediaAsync(client, peer, chatId, query, kinds, limit, ct);

        await PromptAndDownloadAsync(client, items, ct);
    }

    // ── Flow 3: direct link ───────────────────────────────────────────────────
    private async Task DownloadByLinkFlowAsync(CancellationToken ct)
    {
        var link = _prompt.Ask("\nPaste t.me message link: ");
        var client = await GetOwnerClientAsync(ct);
        var (_, chatId, msg) = await _linkResolver.ResolveAsync(client, link, ct);

        var item = MediaItem.TryFrom(chatId, msg);
        if (item is null)
        {
            Console.WriteLine("That message has no downloadable media.\n");
            return;
        }

        Console.WriteLine($"Found: [{item.Kind}] {item.DisplayName} ({FileHelpers.FormatSize(item.Size)})");
        await DownloadListAsync(client, new[] { item }, ct);
    }

    // ── Flow 5: active stories ────────────────────────────────────────────────
    private async Task DownloadActiveStoriesFlowAsync(CancellationToken ct)
    {
        var input = _prompt.Ask("\nEnter user/channel username or ID: ");
        var client = await GetOwnerClientAsync(ct);
        var (peer, peerId) = await _telegram.ResolvePeerAsync(client, input, ct);

        Console.WriteLine("\nFetching active stories…");
        var items = await _telegram.GetActiveStoriesAsync(client, peer, peerId, AllKinds, ct);
        await PromptAndDownloadAsync(client, items, ct);
    }

    // ── Flow 6: pinned stories ────────────────────────────────────────────────
    private async Task DownloadPinnedStoriesFlowAsync(CancellationToken ct)
    {
        var input = _prompt.Ask("\nEnter user/channel username or ID: ");
        var client = await GetOwnerClientAsync(ct);
        var (peer, peerId) = await _telegram.ResolvePeerAsync(client, input, ct);

        var limitStr = _prompt.Ask("How many pinned stories to fetch? [default 50]: ");
        int limit = int.TryParse(limitStr, out var n) ? n : 50;

        Console.WriteLine($"\nFetching up to {limit} pinned stories…");
        var items = await _telegram.GetPinnedStoriesAsync(client, peer, peerId, limit, AllKinds, ct);
        await PromptAndDownloadAsync(client, items, ct);
    }

    // ── Flow 4: list chats ────────────────────────────────────────────────────
    private async Task ListChatsFlowAsync(CancellationToken ct)
    {
        var client = await GetOwnerClientAsync(ct);
        await _telegram.ListChatsAsync(client, ct);
    }

    // ── Flow 7: URL via yt-dlp ────────────────────────────────────────────────
    private async Task DownloadFromUrlFlowAsync(CancellationToken ct)
    {
        var url = _prompt.Ask("\nPaste video URL (YouTube, Instagram, TikTok, …): ");
        if (string.IsNullOrWhiteSpace(url))
        {
            Console.WriteLine("No URL provided.\n");
            return;
        }

        var modeRaw = _prompt.Ask("Download mode [v=video, a=audio only] [default v]: ");
        bool audioOnly = !string.IsNullOrWhiteSpace(modeRaw)
                         && modeRaw.Trim().StartsWith("a", StringComparison.OrdinalIgnoreCase);

        try
        {
            var path = await _webDownloader.DownloadAsync(url, audioOnly, ct);
            Console.WriteLine($"Done. Saved to: {path}\n");
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _logger.LogError(ex, "URL download failed");
        }
    }

    // ── Shared selection + dispatch ───────────────────────────────────────────
    private async Task PromptAndDownloadAsync(Client client, IReadOnlyList<MediaItem> items, CancellationToken ct)
    {
        if (items.Count == 0)
        {
            Console.WriteLine("No matching media found.\n");
            return;
        }

        Console.WriteLine($"\nFound {items.Count} item(s):\n");
        for (int i = 0; i < items.Count; i++)
        {
            var it = items[i];
            var dur = it.Duration is { } d ? $"  {d:mm\\:ss}" : "";
            var idLabel = it.IsStory ? $"StoryID={it.MsgId}" : $"MsgID={it.MsgId}";
            Console.WriteLine($"  [{i + 1}] [{it.Kind,-8}] {idLabel}  {it.DisplayName}  {FileHelpers.FormatSize(it.Size)}{dur}");
        }

        var sel = _prompt.Ask("\nWhich to download? (e.g. 1,3 or 1-5 or 'all', empty to cancel): ");
        if (string.IsNullOrWhiteSpace(sel)) return;

        var indices = ParseSelection(sel, items.Count).ToList();
        var selected = indices.Select(i => items[i]).ToList();

        await DownloadListAsync(client, selected, ct);
    }

    private async Task DownloadListAsync(Client client, IReadOnlyList<MediaItem> selected, CancellationToken ct)
    {
        if (selected.Count == 0) return;

        Console.WriteLine($"\nStarting {selected.Count} download(s)…\n");
        int skipped = await _telegram.DownloadManyAsync(client, selected,
            onCompleted: (done, total) =>
            {
                Console.Write($"\r  progress: {done}/{total} completed   ");
            },
            ct);

        Console.WriteLine();
        Console.WriteLine($"Done. Downloaded: {selected.Count - skipped}, skipped (already present): {skipped}.\n");
    }

    // ── Helpers ───────────────────────────────────────────────────────────────
    private IReadOnlySet<MediaKind> AskMediaKinds()
    {
        var raw = _prompt.Ask("Media kinds [v=video, p=photo, a=audio, d=document, all] [default v]: ");
        if (string.IsNullOrWhiteSpace(raw)) raw = "v";
        if (raw.Equals("all", StringComparison.OrdinalIgnoreCase))
            return new HashSet<MediaKind> { MediaKind.Video, MediaKind.Photo, MediaKind.Audio, MediaKind.Document };

        var set = new HashSet<MediaKind>();
        foreach (var ch in raw.ToLowerInvariant())
        {
            switch (ch)
            {
                case 'v': set.Add(MediaKind.Video); break;
                case 'p': set.Add(MediaKind.Photo); break;
                case 'a': set.Add(MediaKind.Audio); break;
                case 'd': set.Add(MediaKind.Document); break;
            }
        }
        if (set.Count == 0) set.Add(MediaKind.Video);
        return set;
    }

    private static IEnumerable<int> ParseSelection(string sel, int max)
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

    public static void PrintProgress(long transferred, long total, TimeSpan elapsed)
    {
        double pct = total > 0 ? (double)transferred / total * 100 : 0;
        double mbps = elapsed.TotalSeconds > 0
                       ? transferred / 1_048_576.0 / elapsed.TotalSeconds
                       : 0;
        int bars = (int)(pct / (100.0 / ProgressBarWidth));
        string bar = $"[{new string('█', bars)}{new string('░', ProgressBarWidth - bars)}]";
        Console.Write($"\r  {bar} {pct,5:F1}%  {FileHelpers.FormatSize(transferred)}/{FileHelpers.FormatSize(total)}  {mbps:F2} MB/s  ");
    }
}
