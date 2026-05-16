using Application.Sessions;

namespace Bot;

/// <summary>
/// Per-chat conversation state for the bot. Tracks which login step the user
/// is on so the next plain-text message can be routed to the right
/// <see cref="LoginSession"/> TCS.
/// </summary>
internal sealed class BotConversation
{
    public long ChatId { get; }
    public long UserId { get; }
    public LoginSession? Login { get; set; }
    public LoginStep Step { get; set; } = LoginStep.Idle;

    public BotConversation(long chatId, long userId)
    {
        ChatId = chatId;
        UserId = userId;
    }
}

internal enum LoginStep
{
    Idle,
    AwaitingPhone,
    AwaitingCode,
    AwaitingPassword,
}
