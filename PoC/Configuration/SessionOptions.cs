namespace TelegramDownloader.Configuration;

internal sealed class SessionOptions
{
    public const string SectionName = "Sessions";

    /// <summary>Base64-encoded 32-byte AES-GCM key. Required.</summary>
    public string EncryptionKey { get; set; } = "";

    /// <summary>Maximum number of WTelegramClient instances kept alive at once.</summary>
    public int MaxConcurrentSessions { get; set; } = 200;

    /// <summary>Idle clients are evicted (disconnected) after this many minutes.</summary>
    public int IdleEvictionMinutes { get; set; } = 15;
}
