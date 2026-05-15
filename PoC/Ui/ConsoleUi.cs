using System.Text.RegularExpressions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using TelegramDownloader.Helpers;
using TelegramDownloader.Services;
using TL;

namespace TelegramDownloader.Ui;

internal sealed class ConsoleUi : BackgroundService
{
    private const int ProgressBarWidth = 50;
    private const int DefaultMessageScanLimit = 50;

    private readonly TelegramService _telegram;
    private readonly IConsolePrompt _prompt;
    private readonly IHostApplicationLifetime _lifetime;
    private readonly ILogger<ConsoleUi> _logger;

    public ConsoleUi(
        TelegramService telegram,
        IConsolePrompt prompt,
        IHostApplicationLifetime lifetime,
        ILogger<ConsoleUi> logger)
    {
        _telegram = telegram;
        _prompt = prompt;
        _lifetime = lifetime;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await _telegram.ConnectAsync(stoppingToken);
            await RunMenuAsync(stoppingToken);
        }
        catch (OperationCanceledException)
        {
            // graceful shutdown
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Fatal error in console UI");
        }
        finally
        {
            _lifetime.StopApplication();
        }
    }

    private async Task RunMenuAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            Console.WriteLine("════════════════════════════════════");
            Console.WriteLine("1 – Download video from a chat/channel");
            Console.WriteLine("2 – List recent chats");
            Console.WriteLine("0 – Exit");
            Console.Write("Choice: ");
            var choice = _prompt.ReadLineTrimmed();

            switch (choice)
            {
                case "1": await DownloadVideoFlowAsync(cancellationToken); break;
                case "2": await _telegram.ListChatsAsync(cancellationToken); break;
                case "0": return;
                default: Console.WriteLine("Unknown option.\n"); break;
            }
        }
    }

    private async Task DownloadVideoFlowAsync(CancellationToken cancellationToken)
    {
        var input = _prompt.Ask("\nEnter chat username or ID (e.g. @mychannel or 123456789): ");

        InputPeer peer;
        try
        {
            peer = await _telegram.ResolvePeerAsync(input, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Could not resolve chat '{Input}'", input);
            return;
        }

        var limitStr = _prompt.Ask($"How many recent messages to scan? [default {DefaultMessageScanLimit}]: ");
        int limit = int.TryParse(limitStr, out var n) ? n : DefaultMessageScanLimit;

        Console.WriteLine($"\nFetching last {limit} messages…");
        var videos = await _telegram.GetVideosAsync(peer, limit, cancellationToken);

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
            var size = FileHelpers.FormatSize(doc.size);
            var dur = video is not null ? TimeSpan.FromSeconds(video.duration).ToString(@"mm\:ss") : "?";
            Console.WriteLine($"  [{i + 1}] MsgID={msgId}  {name}  {size}  {dur}");
        }

        var sel = _prompt.Ask("\nWhich video(s) to download? (e.g. 1,3 or 1-5 or 'all'): ");
        var indices = ParseSelection(sel, videos.Count);

        foreach (var idx in indices)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var (msgId, doc) = videos[idx];
            await _telegram.DownloadDocumentAsync(doc, msgId, cancellationToken);
        }
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
