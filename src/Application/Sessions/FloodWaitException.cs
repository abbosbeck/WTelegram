namespace Application.Sessions;

/// <summary>
/// Thrown when Telegram returns FLOOD_WAIT_X. Carries the remaining wait time
/// so the caller can surface it to the user instead of a raw stack trace.
/// </summary>
public sealed class FloodWaitException : Exception
{
    public int Seconds { get; }
    public FloodWaitException(int seconds, string message) : base(message)
    {
        Seconds = seconds;
    }
}
