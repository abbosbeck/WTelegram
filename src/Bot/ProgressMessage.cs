using Microsoft.Extensions.Logging;
using Telegram.Bot;

namespace Bot;

/// <summary>
/// A single status message edited in place to surface progress. Throttles edits
/// to avoid Telegram's MESSAGE_NOT_MODIFIED and per-chat flood limits.
/// </summary>
internal sealed class ProgressMessage
{
    private static readonly TimeSpan MinInterval = TimeSpan.FromMilliseconds(800);

    private readonly ITelegramBotClient _bot;
    private readonly long _chatId;
    private readonly int _messageId;
    private readonly ILogger _logger;

    private readonly Lock _gate = new();
    private string _lastText = "";
    private DateTime _lastEditUtc = DateTime.MinValue;
    private Task _inFlight = Task.CompletedTask;

    public ProgressMessage(ITelegramBotClient bot, long chatId, int messageId, ILogger logger)
    {
        _bot = bot;
        _chatId = chatId;
        _messageId = messageId;
        _logger = logger;
    }

    /// <summary>
    /// Update the status, throttled. Does not throw on transient edit failures.
    /// </summary>
    public Task UpdateAsync(string text, CancellationToken ct)
    {
        lock (_gate)
        {
            var now = DateTime.UtcNow;
            if (text == _lastText) return Task.CompletedTask;
            if (now - _lastEditUtc < MinInterval) return Task.CompletedTask;
            _lastText = text;
            _lastEditUtc = now;
        }
        // Fire and forget; we don't want to block the producer on Telegram latency.
        _inFlight = EditAsync(text, ct);
        return _inFlight;
    }

    /// <summary>
    /// Final state: forces the edit through ignoring the throttle and awaits prior edits.
    /// </summary>
    public async Task FinalizeAsync(string text, CancellationToken ct)
    {
        try { await _inFlight; } catch { /* ignore */ }
        lock (_gate)
        {
            _lastText = text;
            _lastEditUtc = DateTime.UtcNow;
        }
        await EditAsync(text, ct);
    }

    private async Task EditAsync(string text, CancellationToken ct)
    {
        try
        {
            await _bot.EditMessageText(_chatId, _messageId, text, cancellationToken: ct);
        }
        catch (OperationCanceledException) { /* swallow */ }
        catch (Exception ex) { _logger.LogDebug(ex, "Progress edit failed"); }
    }
}
