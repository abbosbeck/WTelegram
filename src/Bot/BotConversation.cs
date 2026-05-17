using Application.Sessions;

namespace Bot;

/// <summary>
/// Per-chat conversation state for the bot. Tracks which login step the user
/// is on so the next plain-text message can be routed to the right
/// <see cref="LoginSession"/> TCS, plus a pending action set by menu buttons
/// (e.g. "next text from this user is a t.me link for /by_link").
/// </summary>
internal sealed class BotConversation
{
    public long ChatId { get; }
    public long UserId { get; }
    public LoginSession? Login { get; set; }
    public LoginStep Step { get; set; } = LoginStep.Idle;

    /// <summary>What the bot is waiting for from the user's next plain-text message.</summary>
    public PendingAction Pending { get; set; } = PendingAction.None;

    /// <summary>Auxiliary scratch used by multi-step pending flows (e.g. /search target).</summary>
    public string? PendingArg { get; set; }

    public BotConversation(long chatId, long userId)
    {
        ChatId = chatId;
        UserId = userId;
    }

    public void ClearPending()
    {
        Pending = PendingAction.None;
        PendingArg = null;
    }
}

internal enum LoginStep
{
    Idle,
    AwaitingPhone,
    AwaitingCode,
    AwaitingPassword,
}

internal enum PendingAction
{
    None,
    /// <summary>User asked for the unified download flow; classify Telegram vs web on the next message.</summary>
    AwaitingDownloadLink,
    AwaitingChatTarget,
    AwaitingSearchTarget,
    AwaitingSearchQuery,
    AwaitingStoriesTarget,
    AwaitingStoriesPinnedTarget,
}
