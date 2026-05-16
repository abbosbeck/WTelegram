using System.Collections.Concurrent;
using Application.Configuration;
using Application.Sessions;
using Infrastructure.Sessions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Telegram.Bot;
using Telegram.Bot.Polling;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;

namespace Bot;

/// <summary>
/// Long-polling Telegram bot worker. Drives the per-user MTProto login through
/// chat messages, routed via <see cref="LoginCoordinator"/>.
/// </summary>
internal sealed class BotUpdateHandler : BackgroundService
{
    private readonly ITelegramBotClient _bot;
    private readonly LoginCoordinator _loginCoordinator;
    private readonly SessionPool _sessionPool;
    private readonly IUserSessionStore _sessionStore;
    private readonly ILogger<BotUpdateHandler> _logger;

    private readonly ConcurrentDictionary<long, BotConversation> _conversations = new();

    public BotUpdateHandler(
        ITelegramBotClient bot,
        LoginCoordinator loginCoordinator,
        SessionPool sessionPool,
        IUserSessionStore sessionStore,
        ILogger<BotUpdateHandler> logger)
    {
        _bot = bot;
        _loginCoordinator = loginCoordinator;
        _sessionPool = sessionPool;
        _sessionStore = sessionStore;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var me = await _bot.GetMe(stoppingToken);
        _logger.LogInformation("Bot started as @{Username} (id {Id})", me.Username, me.Id);

        var receiverOptions = new ReceiverOptions
        {
            AllowedUpdates = [UpdateType.Message],
            DropPendingUpdates = true,
        };

        await _bot.ReceiveAsync(
            updateHandler: HandleUpdateAsync,
            errorHandler: HandleErrorAsync,
            receiverOptions: receiverOptions,
            cancellationToken: stoppingToken);
    }

    private async Task HandleUpdateAsync(ITelegramBotClient bot, Update update, CancellationToken ct)
    {
        if (update.Message is not { } message) return;
        if (message.From is null) return;
        if (string.IsNullOrEmpty(message.Text)) return;

        var chatId = message.Chat.Id;
        var userId = message.From.Id;
        var text = message.Text.Trim();

        var convo = _conversations.GetOrAdd(chatId, _ => new BotConversation(chatId, userId));

        try
        {
            if (text.StartsWith('/'))
            {
                var cmd = text.Split(' ', 2)[0].Split('@')[0].ToLowerInvariant();
                switch (cmd)
                {
                    case "/start":
                    case "/help":
                        await SendHelpAsync(chatId, ct);
                        return;
                    case "/login":
                        await HandleLoginAsync(convo, ct);
                        return;
                    case "/cancel":
                        await HandleCancelAsync(convo, ct);
                        return;
                    case "/logout":
                        await HandleLogoutAsync(convo, ct);
                        return;
                    case "/status":
                        await HandleStatusAsync(convo, ct);
                        return;
                    default:
                        await _bot.SendMessage(chatId,
                            "Unknown command. Try /login, /cancel, /logout, /status, /help.",
                            cancellationToken: ct);
                        return;
                }
            }

            // Plain text — interpret per current login step.
            await HandleLoginInputAsync(convo, text, message.MessageId, ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error handling update for chat {ChatId}", chatId);
            try
            {
                await _bot.SendMessage(chatId,
                    $"Something went wrong: {ex.Message}",
                    cancellationToken: ct);
            }
            catch { /* ignore secondary failures */ }
        }
    }

    private Task HandleErrorAsync(ITelegramBotClient bot, Exception ex, CancellationToken ct)
    {
        _logger.LogError(ex, "Polling error");
        return Task.CompletedTask;
    }

    private static string ExtractDigits(string input)
    {
        Span<char> buffer = stackalloc char[input.Length];
        int n = 0;
        foreach (var ch in input)
            if (ch >= '0' && ch <= '9') buffer[n++] = ch;
        return new string(buffer[..n]);
    }

    private async Task TryDeleteMessageAsync(long chatId, int messageId, CancellationToken ct)
    {
        try
        {
            await _bot.DeleteMessage(chatId, messageId, ct);
        }
        catch (Exception ex)
        {
            // Bots can delete their own messages always, and user messages only within 48h
            // and only if the bot is admin in groups. Private chats allow it. Log and move on.
            _logger.LogDebug(ex, "Failed to delete message {MessageId} in chat {ChatId}", messageId, chatId);
        }
    }

    private async Task SendHelpAsync(long chatId, CancellationToken ct)
    {
        const string help =
            "Telegram Downloader Bot\n\n" +
            "Commands:\n" +
            "  /login   – sign in to Telegram MTProto (phone + code + 2FA)\n" +
            "  /cancel  – cancel an in-progress login\n" +
            "  /logout  – forget your stored session\n" +
            "  /status  – show current session state\n" +
            "  /help    – this message\n\n" +
            "Download commands land in Phase 3.";
        await _bot.SendMessage(chatId, help, cancellationToken: ct);
    }

    private async Task HandleLoginAsync(BotConversation convo, CancellationToken ct)
    {
        if (_sessionPool.IsCached(convo.UserId))
        {
            await _bot.SendMessage(convo.ChatId,
                "You're already signed in. Use /logout first if you want to switch accounts.",
                cancellationToken: ct);
            return;
        }
        if (convo.Step != LoginStep.Idle)
        {
            await _bot.SendMessage(convo.ChatId,
                "A login is already in progress. Send the requested value or /cancel.",
                cancellationToken: ct);
            return;
        }

        // A previous attempt may have left a stale LoginSession behind
        // (all TCSes already completed with the old phone/code). Drop it so
        // we start the credentials flow from a clean state.
        _loginCoordinator.Cancel(convo.UserId);

        var login = new LoginSession(convo.UserId);
        if (!_loginCoordinator.TryRegister(convo.UserId, login))
            login = _loginCoordinator.Get(convo.UserId)!;

        convo.Login = login;
        convo.Step = LoginStep.AwaitingPhone;

        // Kick off the MTProto login in the background. Result is observed when
        // AcquireAsync completes (either after code or after 2FA password).
        _ = Task.Run(async () =>
        {
            try
            {
                await _sessionPool.AcquireAsync(convo.UserId, ct);
                await NotifyLoginCompletedAsync(convo, success: true, message: null);
            }
            catch (OperationCanceledException)
            {
                await NotifyLoginCompletedAsync(convo, success: false, message: "Login cancelled.");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Login failed for user {UserId}", convo.UserId);
                await NotifyLoginCompletedAsync(convo, success: false, message: ex.Message);
            }
        }, CancellationToken.None);

        await _bot.SendMessage(convo.ChatId,
            "Send your phone number in international format (e.g. +998901234567).",
            cancellationToken: ct);
    }

    private async Task HandleLoginInputAsync(BotConversation convo, string text, int messageId, CancellationToken ct)
    {
        if (convo.Login is null || convo.Step == LoginStep.Idle)
        {
            await _bot.SendMessage(convo.ChatId,
                "Nothing to do. Send /login to begin.",
                cancellationToken: ct);
            return;
        }

        switch (convo.Step)
        {
            case LoginStep.AwaitingPhone:
                convo.Login.SubmitPhone(text);
                convo.Step = LoginStep.AwaitingCode;
                await _bot.SendMessage(convo.ChatId,
                    "Telegram is sending a verification code.\n" +
                    "On a fresh device, Telegram refuses to deliver the first code via chat. " +
                    "Open the official Telegram app → Settings → Devices → the new session entry; " +
                    "the code is part of that entry's title.\n\n" +
                    "⚠️ Telegram auto-invalidates any login code that appears as plain digits " +
                    "inside a chat. To avoid that, send the code with separators between digits, " +
                    "e.g. <code>1-2-3-4-5</code> or <code>1 2 3 4 5</code>. " +
                    "I'll strip the separators before submitting it.",
                    parseMode: ParseMode.Html,
                    cancellationToken: ct);
                return;

            case LoginStep.AwaitingCode:
                var code = ExtractDigits(text);
                if (code.Length == 0)
                {
                    await _bot.SendMessage(convo.ChatId,
                        "That doesn't look like a code. Send the digits with separators, e.g. 1-2-3-4-5.",
                        cancellationToken: ct);
                    return;
                }
                // Delete the user's message so the (separated) code doesn't sit in chat history.
                await TryDeleteMessageAsync(convo.ChatId, messageId, ct);
                convo.Login.SubmitCode(code);
                // After code, either 2FA password is needed or login completes.
                // Race the AcquireAsync completion against the password TCS being awaited.
                _ = Task.Run(() => WaitForPasswordPromptAsync(convo, ct), CancellationToken.None);
                await _bot.SendMessage(convo.ChatId, "Verifying…", cancellationToken: ct);
                return;

            case LoginStep.AwaitingPassword:
                // Delete the user's message so the 2FA password doesn't linger in chat history.
                await TryDeleteMessageAsync(convo.ChatId, messageId, ct);
                convo.Login.SubmitPassword(text);
                convo.Step = LoginStep.Idle;
                await _bot.SendMessage(convo.ChatId, "Verifying 2FA…", cancellationToken: ct);
                return;

            default:
                return;
        }
    }

    private async Task WaitForPasswordPromptAsync(BotConversation convo, CancellationToken ct)
    {
        // Poll until either (a) the login session signals password is awaited
        // (=> 2FA enabled) or (b) the session shows up cached (=> login done).
        for (int i = 0; i < 200 && !ct.IsCancellationRequested; i++)
        {
            if (_sessionPool.IsCached(convo.UserId)) return; // success path handled by AcquireAsync continuation
            if (convo.Login is { IsPasswordAwaited: true })
            {
                convo.Step = LoginStep.AwaitingPassword;
                try
                {
                    await _bot.SendMessage(convo.ChatId,
                        "Two-factor authentication is enabled. Send your 2FA password.",
                        cancellationToken: ct);
                }
                catch { /* ignore */ }
                return;
            }
            await Task.Delay(100, ct);
        }
    }

    private async Task NotifyLoginCompletedAsync(BotConversation convo, bool success, string? message)
    {
        convo.Step = LoginStep.Idle;
        convo.Login = null;
        if (!success)
        {
            // Drop the dead LoginSession so the next /login starts fresh.
            _loginCoordinator.Cancel(convo.UserId);
        }
        var text = success
            ? "✅ Signed in. Your session is stored encrypted in Postgres."
            : $"❌ Login failed: {message}";
        try
        {
            await _bot.SendMessage(convo.ChatId, text);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to send login result to chat {ChatId}", convo.ChatId);
        }
    }

    private async Task HandleCancelAsync(BotConversation convo, CancellationToken ct)
    {
        if (convo.Step == LoginStep.Idle && convo.Login is null)
        {
            await _bot.SendMessage(convo.ChatId, "Nothing to cancel.", cancellationToken: ct);
            return;
        }
        _loginCoordinator.Cancel(convo.UserId);
        convo.Step = LoginStep.Idle;
        convo.Login = null;
        await _bot.SendMessage(convo.ChatId, "Login cancelled.", cancellationToken: ct);
    }

    private async Task HandleLogoutAsync(BotConversation convo, CancellationToken ct)
    {
        await _sessionPool.EvictAsync(convo.UserId);
        await _sessionStore.DeleteAsync(convo.UserId, ct);
        convo.Step = LoginStep.Idle;
        convo.Login = null;
        await _bot.SendMessage(convo.ChatId,
            "Signed out. Your encrypted session has been removed from the database.",
            cancellationToken: ct);
    }

    private async Task HandleStatusAsync(BotConversation convo, CancellationToken ct)
    {
        var cached = _sessionPool.IsCached(convo.UserId);
        var step = convo.Step;
        var msg =
            $"User ID: {convo.UserId}\n" +
            $"Session cached: {cached}\n" +
            $"Login step: {step}";
        await _bot.SendMessage(convo.ChatId, msg, cancellationToken: ct);
    }
}
