namespace Application.Sessions;

/// <summary>
/// Thrown when a stored Telegram session is no longer valid (revoked, expired,
/// password changed, …) and WTelegramClient asks for fresh credentials while
/// the caller did not start a <see cref="LoginSession"/>. The driver should
/// surface this to the user and prompt them to log in again.
/// </summary>
public sealed class SessionExpiredException : Exception
{
    public long UserId { get; }

    public SessionExpiredException(long userId)
        : base($"The stored Telegram session for user {userId} is no longer valid. Please log in again.")
    {
        UserId = userId;
    }
}
