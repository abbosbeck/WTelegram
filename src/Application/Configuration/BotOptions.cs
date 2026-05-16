namespace Application.Configuration;

/// <summary>
/// Configuration for the Telegram Bot host (Phase 2).
/// </summary>
public sealed class BotOptions
{
    public const string SectionName = "Bot";

    /// <summary>Token from @BotFather. Required.</summary>
    public string Token { get; set; } = "";
}
