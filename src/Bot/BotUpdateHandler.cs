using System.Collections.Concurrent;
using Application.Configuration;
using Application.Sessions;
using Domain.Common;
using Domain.Downloads;
using Infrastructure.Downloads;
using Infrastructure.Sessions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Telegram.Bot;
using Telegram.Bot.Polling;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using Telegram.Bot.Types.ReplyMarkups;
using TL;
using TgUpdate = Telegram.Bot.Types.Update;
using TgContact = Telegram.Bot.Types.Contact;
using TgReplyKeyboardMarkup = Telegram.Bot.Types.ReplyMarkups.ReplyKeyboardMarkup;
using TgKeyboardButton = Telegram.Bot.Types.ReplyMarkups.KeyboardButton;

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
    private readonly TelegramService _telegram;
    private readonly MessageLinkResolver _linkResolver;
    private readonly WebVideoDownloader _webDownloader;
    private readonly BotMediaSender _sender;
    private readonly MediaSelectionCache _selections;
    private readonly PendingActionCache _pending;
    private readonly TelegramOptions _telegramOptions;
    private readonly ILogger<BotUpdateHandler> _logger;

    private readonly ConcurrentDictionary<long, BotConversation> _conversations = new();
    private readonly ConcurrentDictionary<long, InputPeer> _botPeerPerUser = new();
    private string? _botUsername;

    public BotUpdateHandler(
        ITelegramBotClient bot,
        LoginCoordinator loginCoordinator,
        SessionPool sessionPool,
        IUserSessionStore sessionStore,
        TelegramService telegram,
        MessageLinkResolver linkResolver,
        WebVideoDownloader webDownloader,
        BotMediaSender sender,
        MediaSelectionCache selections,
        PendingActionCache pending,
        IOptions<TelegramOptions> telegramOptions,
        ILogger<BotUpdateHandler> logger)
    {
        _bot = bot;
        _loginCoordinator = loginCoordinator;
        _sessionPool = sessionPool;
        _sessionStore = sessionStore;
        _telegram = telegram;
        _linkResolver = linkResolver;
        _webDownloader = webDownloader;
        _sender = sender;
        _selections = selections;
        _pending = pending;
        _telegramOptions = telegramOptions.Value;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var me = await _bot.GetMe(stoppingToken);
        _botUsername = me.Username;
        _logger.LogInformation("Bot started as @{Username} (id {Id})", me.Username, me.Id);

        // Populate the "/" command menu in every Telegram client.
        try
        {
            await _bot.SetMyCommands(new Telegram.Bot.Types.BotCommand[]
            {
                new() { Command = "menu",   Description = "Open the action menu" },
                new() { Command = "login",  Description = "Sign in to your Telegram account" },
                new() { Command = "status", Description = "Show your session state" },
                new() { Command = "logout", Description = "Forget your stored session" },
                new() { Command = "cancel", Description = "Cancel the current operation" },
                new() { Command = "help",   Description = "Show command reference" },
            }, cancellationToken: stoppingToken);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "SetMyCommands failed; \"/\" menu may be stale.");
        }

        var receiverOptions = new ReceiverOptions
        {
            AllowedUpdates = [UpdateType.Message, UpdateType.CallbackQuery],
            DropPendingUpdates = true,
        };

        await _bot.ReceiveAsync(
            updateHandler: HandleUpdateAsync,
            errorHandler: HandleErrorAsync,
            receiverOptions: receiverOptions,
            cancellationToken: stoppingToken);
    }

    private async Task HandleUpdateAsync(ITelegramBotClient bot, TgUpdate update, CancellationToken ct)
    {
        if (update.CallbackQuery is { } cb)
        {
            await HandleCallbackAsync(cb, ct);
            return;
        }

        if (update.Message is not { } message) return;
        if (message.From is null) return;

        var chatId = message.Chat.Id;
        var userId = message.From.Id;
        var convo = _conversations.GetOrAdd(chatId, _ => new BotConversation(chatId, userId));

        // Native "Share contact" tap arrives as a Contact message (no Text).
        if (message.Contact is { } contact)
        {
            try { await HandleSharedContactAsync(convo, contact, ct); }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to handle shared contact for chat {ChatId}", chatId);
                try { await _bot.SendMessage(chatId, $"Couldn't process the contact: {ex.Message}", cancellationToken: ct); }
                catch { /* ignore */ }
            }
            return;
        }

        if (string.IsNullOrEmpty(message.Text)) return;
        var text = message.Text.Trim();

        try
        {
            if (text.StartsWith('/'))
            {
                var cmd = text.Split(' ', 2)[0].Split('@')[0].ToLowerInvariant();
                switch (cmd)
                {
                    case "/start":
                    case "/menu":
                        await HandleMenuAsync(convo, ct);
                        return;
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
                    case "/by_link":
                        await HandleByLinkAsync(convo, text, ct);
                        return;
                    case "/url":
                        await HandleUrlAsync(convo, text, ct);
                        return;
                    case "/chat":
                        await HandleChatAsync(convo, text, ct);
                        return;
                    case "/search":
                        await HandleSearchAsync(convo, text, ct);
                        return;
                    case "/stories":
                        await HandleStoriesAsync(convo, text, ct);
                        return;
                    default:
                        await _bot.SendMessage(chatId,
                            "Unknown command. Try /menu or /help.",
                            cancellationToken: ct);
                        return;
                }
            }

            // Plain text — dispatch to a pending menu-driven action first,
            // then fall back to the login state machine.
            if (await TryHandlePendingAsync(convo, text, message.MessageId, ct))
                return;
            await HandleLoginInputAsync(convo, text, message.MessageId, ct);
        }
        catch (SessionExpiredException)
        {
            convo.ClearPending();
            convo.Step = LoginStep.Idle;
            convo.Login = null;
            try
            {
                await _bot.SendMessage(chatId,
                    "⚠️ Your Telegram session is no longer valid (revoked, password changed, or expired).\n\n" +
                    "Send /login to sign in again.",
                    cancellationToken: ct);
            }
            catch { /* ignore */ }
        }
        catch (Exception ex)
        {
            // Some libraries wrap our exception; check inner chain too.
            if (FindInner<SessionExpiredException>(ex) is not null)
            {
                convo.ClearPending();
                convo.Step = LoginStep.Idle;
                convo.Login = null;
                try
                {
                    await _bot.SendMessage(chatId,
                        "⚠️ Your Telegram session is no longer valid.\n\nSend /login to sign in again.",
                        cancellationToken: ct);
                }
                catch { /* ignore */ }
                return;
            }
            _logger.LogError(ex, "Error handling update for chat {ChatId}", chatId);
            try
            {
                await _bot.SendMessage(chatId,
                    $"Something went wrong: {ex.Message}",
                    cancellationToken: ct);
            }
            catch { /* ignore secondary failures */ }
            await SendMenuAsync(chatId, "What would you like to do next?", ct);
        }
    }

    private static T? FindInner<T>(Exception? ex) where T : Exception
    {
        for (var e = ex; e is not null; e = e.InnerException)
            if (e is T t) return t;
        return null;
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
            "  /login                       – sign in to Telegram MTProto\n" +
            "  /by_link <t.me/…>            – fetch one message into this chat\n" +
            "  /chat <@user|id> [limit]     – list recent media in a chat\n" +
            "  /search <@chat> <query>      – search media in a chat\n" +
            "  /stories <@user> [pinned]    – fetch active or pinned stories\n" +
            "  /url <link>                  – download from YouTube/TikTok/… via yt-dlp\n" +
            "  /cancel /logout /status      – session control\n" +
            "  /help                        – this message";
        await _bot.SendMessage(chatId, help, cancellationToken: ct);
    }

    private async Task HandleLoginAsync(BotConversation convo, CancellationToken ct)
    {
        if (_sessionPool.IsCached(convo.UserId) || await _sessionStore.ExistsAsync(convo.UserId, ct))
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

        var keyboard = new TgReplyKeyboardMarkup(
            new[]
            {
                new[] { TgKeyboardButton.WithRequestContact("📱 Share my phone number") }
            })
        { ResizeKeyboard = true, OneTimeKeyboard = true };

        await _bot.SendMessage(convo.ChatId,
            "Tap the button below to share your phone number, or type it in international format (e.g. <code>+998901234567</code>).",
            parseMode: ParseMode.Html,
            replyMarkup: keyboard,
            cancellationToken: ct);
    }

    private async Task HandleSharedContactAsync(BotConversation convo, TgContact contact, CancellationToken ct)
    {
        // No login in progress — dismiss the keyboard and ignore.
        if (convo.Login is null || convo.Step != LoginStep.AwaitingPhone)
        {
            await _bot.SendMessage(convo.ChatId,
                "No login in progress. Send /login to begin.",
                replyMarkup: new ReplyKeyboardRemove(),
                cancellationToken: ct);
            return;
        }

        // Reject sharing somebody else's contact — wouldn't authenticate anyway.
        if (contact.UserId is long uid && uid != convo.UserId)
        {
            await _bot.SendMessage(convo.ChatId,
                "Please share your *own* phone number, not someone else's.",
                parseMode: ParseMode.Markdown,
                replyMarkup: new ReplyKeyboardRemove(),
                cancellationToken: ct);
            return;
        }

        var phone = contact.PhoneNumber.StartsWith("+") ? contact.PhoneNumber : "+" + contact.PhoneNumber;
        convo.Login.SubmitPhone(phone);
        convo.Step = LoginStep.AwaitingCode;

        await _bot.SendMessage(convo.ChatId,
            "Got it ✓\n\n" +
            "Telegram is sending a verification code.\n" +
            "On a fresh device, Telegram refuses to deliver the first code via chat. " +
            "Open the official Telegram app → Settings → Devices → the new session entry; " +
            "the code is part of that entry's title.\n\n" +
            "⚠️ Telegram auto-invalidates any login code that appears as plain digits " +
            "inside a chat. To avoid that, send the code with separators between digits, " +
            "e.g. <code>1-2-3-4-5</code> or <code>1 2 3 4 5</code>. " +
            "I'll strip the separators before submitting it.",
            parseMode: ParseMode.Html,
            replyMarkup: new ReplyKeyboardRemove(),
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
        await SendMenuAsync(convo.ChatId, "What would you like to do next?", CancellationToken.None);
    }

    private async Task HandleCancelAsync(BotConversation convo, CancellationToken ct)
    {
        var hadLogin = convo.Step != LoginStep.Idle || convo.Login is not null;
        var hadPending = convo.Pending != PendingAction.None;
        if (!hadLogin && !hadPending)
        {
            await _bot.SendMessage(convo.ChatId, "Nothing to cancel.", cancellationToken: ct);
            return;
        }
        if (hadLogin)
        {
            _loginCoordinator.Cancel(convo.UserId);
            convo.Step = LoginStep.Idle;
            convo.Login = null;
        }
        convo.ClearPending();
        await _bot.SendMessage(convo.ChatId, "Cancelled.", cancellationToken: ct);
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
        await SendMenuAsync(convo.ChatId, "What would you like to do next?", ct);
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

    private async Task HandleByLinkAsync(BotConversation convo, string text, CancellationToken ct)
    {
        var parts = text.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length < 2)
        {
            await _bot.SendMessage(convo.ChatId,
                "Usage: /by_link <t.me/...> — e.g. /by_link https://t.me/somechannel/42",
                cancellationToken: ct);
            return;
        }
        var link = parts[1];

        if (!await RequireLoginAsync(convo, ct)) return;

        var status = await _bot.SendMessage(convo.ChatId, "Resolving link…", cancellationToken: ct);

        try
        {
            var client = await _sessionPool.AcquireAsync(convo.UserId, ct);
            var (sourcePeer, _, message) = await _linkResolver.ResolveAsync(client, link, ct);

            var botPeer = await GetBotPeerForAsync(client, ct);

            await _bot.EditMessageText(convo.ChatId, status.MessageId,
                "Forwarding via Telegram CDN…", cancellationToken: ct);

            await _telegram.ForwardMessageAsync(
                client,
                source: sourcePeer,
                messageId: message.ID,
                target: botPeer,
                dropAuthor: true,
                ct);

            await _bot.DeleteMessage(convo.ChatId, status.MessageId, ct);
            await SendMenuAsync(convo.ChatId, "Done. What's next?", ct);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) when (FindInner<SessionExpiredException>(ex) is null)
        {
            _logger.LogError(ex, "Forward by link failed for user {UserId}", convo.UserId);
            try
            {
                await _bot.EditMessageText(convo.ChatId, status.MessageId,
                    $"Failed: {ex.Message}", cancellationToken: ct);
            }
            catch { /* ignore */ }
            await SendMenuAsync(convo.ChatId, "What would you like to do next?", ct);
        }
    }

    /// <summary>
    /// Resolves this bot's <see cref="InputPeer"/> as seen by a specific user's
    /// MTProto client. Cached per user — the access_hash is per-DC and per-user.
    /// </summary>
    private async Task<InputPeer> GetBotPeerForAsync(WTelegram.Client client, CancellationToken ct)
    {
        // Find which user this client belongs to by inspecting the cache.
        // Cheaper: ask each call site to pass userId. We do it via the bot username
        // resolved from this user's perspective, which Telegram handles entirely server-side.
        if (string.IsNullOrEmpty(_botUsername))
            throw new InvalidOperationException("Bot username is not known yet.");

        // Keying the cache by client identity avoids the userId plumbing.
        // ConditionalWeakTable would be ideal here, but a plain dictionary keyed
        // by the client's hash code is fine — Clients are long-lived in the pool.
        var key = (long)client.GetHashCode();
        if (_botPeerPerUser.TryGetValue(key, out var cached)) return cached;

        var resolved = await client.Contacts_ResolveUsername(_botUsername).WaitAsync(ct);
        if (resolved.peer is not PeerUser pu || !resolved.users.TryGetValue(pu.user_id, out var botUser))
            throw new InvalidOperationException(
                $"Could not resolve bot @{_botUsername} via the user's account.");

        var peer = botUser.ToInputPeer();
        _botPeerPerUser[key] = peer;
        return peer;
    }

    // ── /url ─────────────────────────────────────────────────────────────────

    private async Task HandleUrlAsync(BotConversation convo, string text, CancellationToken ct)
    {
        var parts = text.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length < 2)
        {
            // No URL provided — reuse the unified download-by-link flow.
            convo.ClearPending();
            convo.Pending = PendingAction.AwaitingDownloadLink;
            await PromptForDownloadLinkAsync(convo, ct);
            return;
        }
        await StartUrlFlowAsync(convo, parts[1], ct);
    }

    /// <summary>
    /// Inspects formats for the URL and presents a quality picker keyboard.
    /// Quality selection then triggers download + upload (with file_id caching).
    /// </summary>
    private async Task StartUrlFlowAsync(BotConversation convo, string url, CancellationToken ct)
    {
        var status = await _bot.SendMessage(convo.ChatId, "Inspecting URL…", cancellationToken: ct);
        try
        {
            var info = await _webDownloader.FetchFormatsAsync(url, ct);
            var token = _pending.StoreUrl(convo.UserId, url, info);

            var rows = new List<InlineKeyboardButton[]>();
            if (info.Heights.Length == 0)
            {
                rows.Add([InlineKeyboardButton.WithCallbackData("📹 Best available", $"q:{token}:best")]);
            }
            else
            {
                // One button per height (descending = best first).
                for (int i = info.Heights.Length - 1; i >= 0; i--)
                {
                    var h = info.Heights[i];
                    var size = info.SizeByHeight.TryGetValue(h, out var s) && s > 0
                        ? $" · ~{FormatSize(s)}"
                        : "";
                    rows.Add([InlineKeyboardButton.WithCallbackData($"📹 {h}p{size}", $"q:{token}:{h}")]);
                }
            }
            var audioLabel = info.ApproxAudioSize is long a && a > 0
                ? $"🎵 Audio only · ~{FormatSize(a)}"
                : "🎵 Audio only";
            rows.Add([InlineKeyboardButton.WithCallbackData(audioLabel, $"q:{token}:a")]);
            rows.Add([InlineKeyboardButton.WithCallbackData("✖ Cancel", $"qx:{token}")]);

            var title = string.IsNullOrWhiteSpace(info.Title) ? "(no title)" : Truncate(info.Title!, 80);
            await _bot.EditMessageText(convo.ChatId, status.MessageId,
                $"<b>{System.Net.WebUtility.HtmlEncode(title)}</b>\nChoose a quality:",
                parseMode: ParseMode.Html,
                replyMarkup: new InlineKeyboardMarkup(rows),
                cancellationToken: ct);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) when (FindInner<SessionExpiredException>(ex) is null)
        {
            _logger.LogError(ex, "/url format probe failed");
            await SafeEditAsync(convo.ChatId, status.MessageId, $"Failed: {ex.Message}", ct);
            await SendMenuAsync(convo.ChatId, "What would you like to do next?", ct);
        }
    }

    private async Task RunUrlDownloadAsync(BotConversation convo, string url, bool audioOnly, int? maxHeight, CancellationToken ct)
    {
        var urlKey = audioOnly ? $"audio:{url}"
            : maxHeight is int h ? $"video:{h}:{url}"
            : $"video:{url}";

        var status = await _bot.SendMessage(convo.ChatId, "Working…", cancellationToken: ct);
        try
        {
            if (await _sender.TrySendCachedUrlAsync(convo.ChatId, urlKey, audioOnly, ct))
            {
                await TryDeleteMessageAsync(convo.ChatId, status.MessageId, ct);
                await SendMenuAsync(convo.ChatId, "Done (from cache). What's next?", ct);
                return;
            }

            await _bot.EditMessageText(convo.ChatId, status.MessageId,
                "Downloading via yt-dlp…", cancellationToken: ct);

            var localPath = await _webDownloader.DownloadAsync(url, audioOnly, maxHeight, onProgress: null, ct);
            if (string.IsNullOrWhiteSpace(localPath) || !File.Exists(localPath))
                throw new InvalidOperationException("yt-dlp returned no output file.");

            await _bot.EditMessageText(convo.ChatId, status.MessageId,
                "Uploading to Telegram (last time — future requests will reuse the cached file)…",
                cancellationToken: ct);

            await _sender.UploadUrlAsync(convo.ChatId, urlKey, localPath, audioOnly, ct);
            await TryDeleteMessageAsync(convo.ChatId, status.MessageId, ct);
            await SendMenuAsync(convo.ChatId, "Done. What's next?", ct);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) when (FindInner<SessionExpiredException>(ex) is null)
        {
            _logger.LogError(ex, "/url download failed");
            await SafeEditAsync(convo.ChatId, status.MessageId, $"Failed: {ex.Message}", ct);
            await SendMenuAsync(convo.ChatId, "What would you like to do next?", ct);
        }
    }

    // ── /chat, /search ───────────────────────────────────────────────────────

    private async Task HandleChatAsync(BotConversation convo, string text, CancellationToken ct)
    {
        if (!await RequireLoginAsync(convo, ct)) return;

        var parts = text.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length < 2)
        {
            await _bot.SendMessage(convo.ChatId,
                "Usage: /chat <@username|id> [limit=20]",
                cancellationToken: ct);
            return;
        }
        var target = parts[1];
        int limit = parts.Length >= 3 && int.TryParse(parts[2], out var n) ? Math.Clamp(n, 1, 50) : 20;

        var status = await _bot.SendMessage(convo.ChatId, "Resolving chat…", cancellationToken: ct);

        try
        {
            var client = await _sessionPool.AcquireAsync(convo.UserId, ct);
            var (peer, chatId) = await _telegram.ResolvePeerAsync(client, target, ct);

            await _bot.EditMessageText(convo.ChatId, status.MessageId,
                $"Fetching last {limit} media items…", cancellationToken: ct);

            var items = await _telegram.GetMediaAsync(client, peer, chatId, limit,
                kinds: new HashSet<MediaKind>(), ct);

            await ShowSelectionAsync(convo, items, MediaSource.ChatMessages, status.MessageId, ct);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) when (FindInner<SessionExpiredException>(ex) is null)
        {
            _logger.LogError(ex, "/chat failed");
            await SafeEditAsync(convo.ChatId, status.MessageId, $"Failed: {ex.Message}", ct);
        }
    }

    private async Task HandleSearchAsync(BotConversation convo, string text, CancellationToken ct)
    {
        if (!await RequireLoginAsync(convo, ct)) return;

        var parts = text.Split(' ', 3, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length < 3)
        {
            await _bot.SendMessage(convo.ChatId,
                "Usage: /search <@username|id> <query>",
                cancellationToken: ct);
            return;
        }
        var target = parts[1];
        var query = parts[2];

        var status = await _bot.SendMessage(convo.ChatId, "Searching…", cancellationToken: ct);

        try
        {
            var client = await _sessionPool.AcquireAsync(convo.UserId, ct);
            var (peer, chatId) = await _telegram.ResolvePeerAsync(client, target, ct);

            var items = await _telegram.SearchMediaAsync(client, peer, chatId, query,
                kinds: new HashSet<MediaKind>(), limit: 50, ct);

            await ShowSelectionAsync(convo, items, MediaSource.ChatMessages, status.MessageId, ct);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) when (FindInner<SessionExpiredException>(ex) is null)
        {
            _logger.LogError(ex, "/search failed");
            await SafeEditAsync(convo.ChatId, status.MessageId, $"Failed: {ex.Message}", ct);
        }
    }

    // ── /stories ─────────────────────────────────────────────────────────────

    private async Task HandleStoriesAsync(BotConversation convo, string text, CancellationToken ct)
    {
        if (!await RequireLoginAsync(convo, ct)) return;

        var parts = text.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length < 2)
        {
            await _bot.SendMessage(convo.ChatId,
                "Usage: /stories <@username|id> [pinned]",
                cancellationToken: ct);
            return;
        }
        var target = parts[1];
        var pinned = parts.Length >= 3 && parts[2].StartsWith("p", StringComparison.OrdinalIgnoreCase);

        var status = await _bot.SendMessage(convo.ChatId,
            pinned ? "Fetching pinned stories…" : "Fetching active stories…",
            cancellationToken: ct);

        try
        {
            var client = await _sessionPool.AcquireAsync(convo.UserId, ct);
            var (peer, peerId) = await _telegram.ResolvePeerAsync(client, target, ct);

            var items = pinned
                ? await _telegram.GetPinnedStoriesAsync(client, peer, peerId, limit: 50,
                    kinds: new HashSet<MediaKind>(), ct)
                : await _telegram.GetActiveStoriesAsync(client, peer, peerId,
                    kinds: new HashSet<MediaKind>(), ct);

            await ShowSelectionAsync(convo, items, MediaSource.Stories, status.MessageId, ct);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) when (FindInner<SessionExpiredException>(ex) is null)
        {
            _logger.LogError(ex, "/stories failed");
            await SafeEditAsync(convo.ChatId, status.MessageId, $"Failed: {ex.Message}", ct);
        }
    }

    // ── Selection rendering + callback handling ──────────────────────────────

    private async Task ShowSelectionAsync(
        BotConversation convo,
        IReadOnlyList<MediaItem> items,
        MediaSource source,
        int statusMessageId,
        CancellationToken ct)
    {
        if (items.Count == 0)
        {
            await SafeEditAsync(convo.ChatId, statusMessageId, "No media found.", ct);
            return;
        }

        var token = _selections.Store(convo.UserId, items, source);

        var rows = new List<InlineKeyboardButton[]>();
        for (int i = 0; i < items.Count; i++)
        {
            var it = items[i];
            var label = $"{KindIcon(it.Kind)} {Truncate(it.DisplayName, 35)} · {FormatSize(it.Size)}";
            rows.Add([InlineKeyboardButton.WithCallbackData(label, $"pick:{token}:{i}")]);
        }
        rows.Add([InlineKeyboardButton.WithCallbackData("⏬ Send all", $"all:{token}")]);

        var markup = new InlineKeyboardMarkup(rows);
        await _bot.EditMessageText(convo.ChatId, statusMessageId,
            $"Found {items.Count} item(s). Tap to send.",
            replyMarkup: markup,
            cancellationToken: ct);
    }

    private async Task HandleCallbackAsync(CallbackQuery cb, CancellationToken ct)
    {
        if (cb.Message is null || string.IsNullOrEmpty(cb.Data))
        {
            await _bot.AnswerCallbackQuery(cb.Id, cancellationToken: ct);
            return;
        }
        var chatId = cb.Message.Chat.Id;
        var userId = cb.From.Id;
        var convo = _conversations.GetOrAdd(chatId, _ => new BotConversation(chatId, userId));

        try
        {
            var parts = cb.Data.Split(':');
            var verb = parts[0];

            switch (verb)
            {
                // Media selection callbacks (from /chat, /search, /stories).
                case "pick" when parts.Length == 3 && int.TryParse(parts[2], out var idx):
                    await _bot.AnswerCallbackQuery(cb.Id, "Sending…", cancellationToken: ct);
                    await SendSelectedAsync(userId, chatId, parts[1], [idx], ct);
                    return;

                case "all" when parts.Length == 2:
                    await _bot.AnswerCallbackQuery(cb.Id, "Sending all…", cancellationToken: ct);
                    var sel = _selections.Get(userId, parts[1]);
                    if (sel is null)
                    {
                        await _bot.SendMessage(chatId, "Selection expired. Re-run the command.", cancellationToken: ct);
                        return;
                    }
                    var all = Enumerable.Range(0, sel.Value.Items.Count).ToArray();
                    await SendSelectedAsync(userId, chatId, parts[1], all, ct);
                    return;

                // Main menu buttons → set pending action and prompt for input.
                case "m" when parts.Length == 2:
                    await _bot.AnswerCallbackQuery(cb.Id, cancellationToken: ct);
                    await HandleMenuButtonAsync(convo, parts[1], cb.Message.MessageId, ct);
                    return;

                // URL quality picker.
                case "q" when parts.Length == 3:
                    await _bot.AnswerCallbackQuery(cb.Id, "Starting…", cancellationToken: ct);
                    await HandleQualityChoiceAsync(convo, parts[1], parts[2], cb.Message.MessageId, ct);
                    return;

                case "qx" when parts.Length == 2:
                    _pending.ForgetUrl(userId, parts[1]);
                    await _bot.AnswerCallbackQuery(cb.Id, "Cancelled", cancellationToken: ct);
                    await SafeEditAsync(chatId, cb.Message.MessageId, "Cancelled.", ct);
                    return;

                // Cancel from the unified "Download by link" prompt.
                case "dlx" when parts.Length == 1:
                    convo.ClearPending();
                    await _bot.AnswerCallbackQuery(cb.Id, "Cancelled", cancellationToken: ct);
                    await SafeEditAsync(chatId, cb.Message.MessageId, "Cancelled.", ct);
                    await SendMenuAsync(chatId, "What would you like to do?", ct);
                    return;

                default:
                    await _bot.AnswerCallbackQuery(cb.Id, cancellationToken: ct);
                    return;
            }
        }
        catch (Exception ex) when (FindInner<SessionExpiredException>(ex) is null)
        {
            _logger.LogError(ex, "Callback failed: {Data}", cb.Data);
            try { await _bot.AnswerCallbackQuery(cb.Id, $"Failed: {ex.Message}", showAlert: true, cancellationToken: ct); }
            catch { /* ignore */ }
        }
    }

    private async Task SendSelectedAsync(long userId, long chatId, string token, int[] indices, CancellationToken ct)
    {
        var sel = _selections.Get(userId, token);
        if (sel is null)
        {
            await _bot.SendMessage(chatId, "Selection expired. Re-run the command.", cancellationToken: ct);
            return;
        }
        var (items, source) = sel.Value;

        var client = await _sessionPool.AcquireAsync(userId, ct);

        if (source == MediaSource.ChatMessages)
        {
            // Try the cheap path first: forward via Telegram CDN. Falls back to
            // download+upload per item if the source has noforwards restrictions.
            await ForwardOrFallbackAsync(client, chatId, items, indices, ct);
        }
        else
        {
            // Stories can't be forwarded; download + upload + cache file_id.
            foreach (var i in indices)
            {
                if (i < 0 || i >= items.Count) continue;
                await DownloadAndSendAsync(client, chatId, items[i], ct);
            }
        }
    }

    private async Task ForwardOrFallbackAsync(
        WTelegram.Client client, long chatId,
        IReadOnlyList<MediaItem> items, int[] indices, CancellationToken ct)
    {
        var botPeer = await GetBotPeerForAsync(client, ct);

        // Group by source chat so we can batch one forwardMessages call per group.
        var byChat = indices
            .Where(i => i >= 0 && i < items.Count)
            .Select(i => items[i])
            .Where(it => !it.IsStory)
            .GroupBy(it => it.ChatId);

        foreach (var group in byChat)
        {
            try
            {
                var first = group.First();
                // Resolve the source peer from the items' chatId via the client's dialog cache.
                var sourcePeer = await ResolvePeerByChatIdAsync(client, first.ChatId, ct);
                var msgIds = group.Select(it => it.MsgId).ToArray();

                await _telegram.ForwardMessagesAsync(client, sourcePeer, msgIds, botPeer,
                    dropAuthor: true, ct);
            }
            catch (Exception ex) when (ex.Message.Contains("FORWARDS_RESTRICTED", StringComparison.OrdinalIgnoreCase))
            {
                _logger.LogWarning("Forward restricted; falling back to download+upload for {Count} items", group.Count());
                foreach (var it in group)
                    await DownloadAndSendAsync(client, chatId, it, ct);
            }
        }
    }

    private async Task<InputPeer> ResolvePeerByChatIdAsync(WTelegram.Client client, long chatId, CancellationToken ct)
    {
        var dialogs = await client.Messages_GetAllDialogs().WaitAsync(ct);
        if (dialogs.chats.TryGetValue(chatId, out var c)) return c.ToInputPeer();
        if (dialogs.users.TryGetValue(chatId, out var u)) return u.ToInputPeer();
        throw new InvalidOperationException($"Chat {chatId} not found in dialogs.");
    }

    private async Task DownloadAndSendAsync(WTelegram.Client client, long destChatId, MediaItem item, CancellationToken ct)
    {
        // Fast path: file_id already cached for this (chatId, msgId).
        if (await _sender.TrySendCachedMediaAsync(destChatId, item, ct))
            return;

        // Otherwise: pull bytes from Telegram (user's MTProto), upload via bot, cache file_id.
        await _telegram.DownloadMediaAsync(client, item, onProgress: null, ct);

        var localPath = ResolveLocalPath(item);
        if (!File.Exists(localPath))
            throw new InvalidOperationException($"Downloaded file not found at {localPath}.");

        await _sender.UploadMediaAsync(destChatId, item, localPath, ct);
    }

    private string ResolveLocalPath(MediaItem item)
    {
        // TelegramService.DownloadMediaAsync writes into TelegramOptions.ResolvedOutputDirectory
        // using the sanitized DisplayName. Mirror its naming so we can find the file again.
        var dir = _telegramOptions.ResolvedOutputDirectory;
        var fileName = Domain.Common.FileHelpers.SanitizeFileName(item.DisplayName);
        var primary = Path.Combine(dir, fileName);
        if (File.Exists(primary)) return primary;

        // Collision suffix used by TelegramService when the primary path already existed.
        var stem = Path.GetFileNameWithoutExtension(primary);
        var ext = Path.GetExtension(primary);
        var suffixed = Path.Combine(dir, $"{stem}_{item.MsgId}{ext}");
        return suffixed;
    }

    // ── Menu, pending input dispatch, URL quality picker ─────────────────────

    private async Task HandleMenuAsync(BotConversation convo, CancellationToken ct)
    {
        await SendMenuAsync(convo.ChatId, "What would you like to do?", ct);
    }

    /// <summary>
    /// Sends the main inline keyboard. Used by /menu, /start, and after every
    /// terminal action (success or failure) so the user is never stuck.
    /// </summary>
    private async Task SendMenuAsync(long chatId, string prompt, CancellationToken ct)
    {
        var rows = new InlineKeyboardButton[][]
        {
            [InlineKeyboardButton.WithCallbackData("📥 Download by link", "m:dl")],
            [InlineKeyboardButton.WithCallbackData("▶️ From chat",         "m:chat"),
             InlineKeyboardButton.WithCallbackData("🔍 Search",            "m:search")],
            [InlineKeyboardButton.WithCallbackData("📖 Stories",            "m:stories"),
             InlineKeyboardButton.WithCallbackData("📖 Pinned stories",     "m:pstories")],
            [InlineKeyboardButton.WithCallbackData("📊 Status",             "m:status")],
        };
        try
        {
            await _bot.SendMessage(chatId, prompt,
                replyMarkup: new InlineKeyboardMarkup(rows),
                cancellationToken: ct);
        }
        catch (Exception ex) { _logger.LogDebug(ex, "Send menu failed"); }
    }

    private async Task HandleMenuButtonAsync(BotConversation convo, string action, int menuMessageId, CancellationToken ct)
    {
        // Sub-flows that need a session must verify it first.
        bool needsLogin = action is "dl" or "chat" or "search" or "stories" or "pstories";
        if (needsLogin && !await RequireLoginAsync(convo, ct)) return;

        switch (action)
        {
            case "dl":
                convo.ClearPending();
                convo.Pending = PendingAction.AwaitingDownloadLink;
                await PromptForDownloadLinkAsync(convo, ct);
                return;

            case "chat":
                convo.ClearPending();
                convo.Pending = PendingAction.AwaitingChatTarget;
                await _bot.SendMessage(convo.ChatId,
                    "Send the chat (e.g. <code>@somechannel</code> or numeric id).\n" +
                    "Tip: add a space and a number to set the limit, e.g. <code>@somechannel 40</code>.",
                    parseMode: ParseMode.Html, cancellationToken: ct);
                return;

            case "search":
                convo.ClearPending();
                convo.Pending = PendingAction.AwaitingSearchTarget;
                await _bot.SendMessage(convo.ChatId,
                    "Send the chat to search in (e.g. <code>@somechannel</code>).",
                    parseMode: ParseMode.Html, cancellationToken: ct);
                return;

            case "stories":
                convo.ClearPending();
                convo.Pending = PendingAction.AwaitingStoriesTarget;
                await _bot.SendMessage(convo.ChatId, "Send the user/channel for active stories.", cancellationToken: ct);
                return;

            case "pstories":
                convo.ClearPending();
                convo.Pending = PendingAction.AwaitingStoriesPinnedTarget;
                await _bot.SendMessage(convo.ChatId, "Send the user/channel for pinned stories.", cancellationToken: ct);
                return;

            case "status":
                await HandleStatusAsync(convo, ct);
                return;
        }
    }

    private async Task PromptForDownloadLinkAsync(BotConversation convo, CancellationToken ct)
    {
        var kb = new InlineKeyboardMarkup(
            [[InlineKeyboardButton.WithCallbackData("✖ Cancel", "dlx")]]);
        await _bot.SendMessage(convo.ChatId,
            "Send the link.\n" +
            "• Telegram messages: <code>https://t.me/somechannel/123</code> or <code>@somechannel/123</code>\n" +
            "• Anywhere else: any <code>http(s)://…</code> URL (YouTube, TikTok, Instagram, …)",
            parseMode: ParseMode.Html,
            replyMarkup: kb,
            cancellationToken: ct);
    }

    /// <summary>
    /// If the user has a pending menu action, route the plain text to the
    /// matching command handler. Returns true if the message was consumed.
    /// </summary>
    private async Task<bool> TryHandlePendingAsync(BotConversation convo, string text, int messageId, CancellationToken ct)
    {
        var pending = convo.Pending;
        if (pending == PendingAction.None) return false;

        switch (pending)
        {
            case PendingAction.AwaitingDownloadLink:
                await DispatchDownloadLinkAsync(convo, text, ct);
                return true;

            case PendingAction.AwaitingChatTarget:
                convo.ClearPending();
                await HandleChatAsync(convo, $"/chat {text}", ct);
                return true;

            case PendingAction.AwaitingSearchTarget:
                convo.PendingArg = text;
                convo.Pending = PendingAction.AwaitingSearchQuery;
                await _bot.SendMessage(convo.ChatId,
                    $"Now send the search query for <code>{System.Net.WebUtility.HtmlEncode(text)}</code>.",
                    parseMode: ParseMode.Html, cancellationToken: ct);
                return true;

            case PendingAction.AwaitingSearchQuery:
                var target = convo.PendingArg ?? "";
                convo.ClearPending();
                await HandleSearchAsync(convo, $"/search {target} {text}", ct);
                return true;

            case PendingAction.AwaitingStoriesTarget:
                convo.ClearPending();
                await HandleStoriesAsync(convo, $"/stories {text}", ct);
                return true;

            case PendingAction.AwaitingStoriesPinnedTarget:
                convo.ClearPending();
                await HandleStoriesAsync(convo, $"/stories {text} pinned", ct);
                return true;

            default:
                return false;
        }
    }

    /// <summary>
    /// Classifies the user's link and dispatches to the Telegram or web flow.
    /// On an invalid link we keep <see cref="PendingAction.AwaitingDownloadLink"/>
    /// active so the user can just send another link without re-tapping the menu.
    /// </summary>
    private async Task DispatchDownloadLinkAsync(BotConversation convo, string text, CancellationToken ct)
    {
        var result = LinkClassifier.Classify(text);
        switch (result.Kind)
        {
            case LinkKind.Telegram:
                convo.ClearPending();
                await HandleByLinkAsync(convo, $"/by_link {result.Normalized}", ct);
                return;

            case LinkKind.Web:
                convo.ClearPending();
                await StartUrlFlowAsync(convo, result.Normalized, ct);
                return;

            default:
                // Re-prompt; keep pending state so the next text is tried again.
                var kb = new InlineKeyboardMarkup(
                    [[InlineKeyboardButton.WithCallbackData("✖ Cancel", "dlx")]]);
                await _bot.SendMessage(convo.ChatId,
                    "That doesn't look like a valid link.\n" +
                    "• Telegram messages: <code>https://t.me/somechannel/123</code> or <code>@somechannel/123</code>\n" +
                    "• Anywhere else: any <code>http(s)://…</code> URL\n\n" +
                    "Send another link, or tap Cancel to go back.",
                    parseMode: ParseMode.Html,
                    replyMarkup: kb,
                    cancellationToken: ct);
                return;
        }
    }

    private async Task HandleQualityChoiceAsync(BotConversation convo, string token, string choice, int pickerMessageId, CancellationToken ct)
    {
        var entry = _pending.GetUrl(convo.UserId, token);
        if (entry is null)
        {
            await SafeEditAsync(convo.ChatId, pickerMessageId,
                "This quality picker has expired. Re-run the URL.", ct);
            return;
        }
        var (url, _) = entry.Value;

        bool audioOnly = choice == "a";
        int? maxHeight = audioOnly || choice == "best"
            ? null
            : int.TryParse(choice, out var h) ? h : (int?)null;

        // Replace the picker with a working status so the chat stays clean.
        await SafeEditAsync(convo.ChatId, pickerMessageId,
            audioOnly ? "Audio selected. Working…" :
            maxHeight is int hh ? $"{hh}p selected. Working…" :
            "Best available selected. Working…",
            ct);
        _pending.ForgetUrl(convo.UserId, token);

        await RunUrlDownloadAsync(convo, url, audioOnly, maxHeight, ct);
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Returns true if the user has a session available — either already
    /// rehydrated in-memory, or sitting encrypted in Postgres ready to be loaded.
    /// Only prompts the user to /login when neither is true.
    /// </summary>
    private async Task<bool> RequireLoginAsync(BotConversation convo, CancellationToken ct)
    {
        if (_sessionPool.IsCached(convo.UserId)) return true;
        if (await _sessionStore.ExistsAsync(convo.UserId, ct)) return true;

        await _bot.SendMessage(convo.ChatId,
            "You need to /login first so I can use your Telegram account.",
            cancellationToken: ct);
        return false;
    }

    private async Task SafeEditAsync(long chatId, int messageId, string text, CancellationToken ct)
    {
        try { await _bot.EditMessageText(chatId, messageId, text, cancellationToken: ct); }
        catch (Exception ex) { _logger.LogDebug(ex, "Edit message failed"); }
    }

    private static string KindIcon(MediaKind kind) => kind switch
    {
        MediaKind.Video    => "📹",
        MediaKind.Audio    => "🎵",
        MediaKind.Photo    => "📷",
        _                  => "📄",
    };

    private static string Truncate(string s, int max) =>
        string.IsNullOrEmpty(s) ? "(no name)" :
        s.Length <= max ? s : s[..(max - 1)] + "…";

    private static string FormatSize(long bytes)
    {
        if (bytes <= 0) return "?";
        string[] units = ["B", "KB", "MB", "GB"];
        double v = bytes; int i = 0;
        while (v >= 1024 && i < units.Length - 1) { v /= 1024; i++; }
        return $"{v:0.#} {units[i]}";
    }
}
